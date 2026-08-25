using System.Data;
using System.Security.Claims;
using System.Text.Json;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace IncidentTracker.Api.Modules.Tickets;

/// <summary>
/// Đọc/nạp/map ticket và các tiện ích transaction dùng chung cho mọi service của module.
/// </summary>
public sealed class TicketQueries
{
    private readonly AppDbContext _db;
    private readonly MarkdownRenderer _renderer;

    public TicketQueries(AppDbContext db, MarkdownRenderer renderer)
    {
        _db = db;
        _renderer = renderer;
    }

    public AppDbContext Db => _db;

    // ---------------- Load ----------------

    public IQueryable<Ticket> WithDetails(bool tracking = false)
    {
        var q = _db.Tickets
            .Include(t => t.Project)
            .Include(t => t.Author)
            .Include(t => t.Type)
            .Include(t => t.Milestone)
            .Include(t => t.Labels).ThenInclude(l => l.Label)
            .Include(t => t.Assignees).ThenInclude(a => a.User)
            .Include(t => t.ParentTicket)!.ThenInclude(p => p!.Project);
        return tracking ? q : q.AsNoTracking();
    }

    public async Task<Project> ProjectAsync(string slug, CancellationToken ct)
        => await _db.Projects.FirstOrDefaultAsync(p => p.Slug == slug.ToLowerInvariant(), ct)
           ?? throw AppException.NotFound($"Không tìm thấy project '{slug}'.");

    /// <summary>
    /// Nạp ticket theo <c>project/number</c>. Ticket đã transfer → 301 (BR-REL-05); đã xoá → 404.
    /// </summary>
    public async Task<Ticket> LoadAsync(string slug, int number, bool tracking, CancellationToken ct)
    {
        var s = slug.ToLowerInvariant();
        var ticket = await WithDetails(tracking)
            .FirstOrDefaultAsync(t => t.Project.Slug == s && t.Number == number, ct);
        if (ticket is not null)
        {
            return ticket;
        }

        var redirect = await (
            from r in _db.TicketRedirects
            join p in _db.Projects on r.ProjectId equals p.Id
            join t in _db.Tickets on r.TicketId equals t.Id
            where p.Slug == s && r.Number == number
            select new { t.Project.Slug, t.Number }).FirstOrDefaultAsync(ct);

        if (redirect is not null)
        {
            var location = $"/api/projects/{redirect.Slug}/tickets/{redirect.Number}";
            throw new AppException(StatusCodes.Status301MovedPermanently, "Ticket đã được chuyển",
                $"Ticket {s}#{number} đã chuyển sang {redirect.Slug}#{redirect.Number}.",
                new Dictionary<string, object?> { ["location"] = location, ["project"] = redirect.Slug, ["number"] = redirect.Number });
        }

        throw AppException.NotFound($"Không tìm thấy ticket {s}#{number}.");
    }

    public async Task<Ticket> LoadByIdAsync(Guid id, bool tracking, CancellationToken ct)
        => await WithDetails(tracking).FirstOrDefaultAsync(t => t.Id == id, ct)
           ?? throw AppException.NotFound($"Không tìm thấy ticket '{id}'.");

    /// <summary>Phân giải <c>N</c> hoặc <c>project#N</c> trong phạm vi project mặc định.</summary>
    public async Task<Ticket> ResolveRefAsync(string reference, string defaultSlug, CancellationToken ct)
    {
        var text = reference.Trim().TrimStart('#');
        string slug = defaultSlug;
        var hash = text.IndexOf('#');
        if (hash > 0)
        {
            slug = text[..hash];
            text = text[(hash + 1)..];
        }
        if (!int.TryParse(text, out var number))
        {
            throw AppException.BadRequest($"Tham chiếu ticket '{reference}' không hợp lệ (dạng N hoặc project#N).");
        }
        return await LoadAsync(slug, number, tracking: true, ct);
    }

    // ---------------- Transaction helpers ----------------

    /// <summary>
    /// <c>EnableRetryOnFailure</c> không cho phép transaction do người dùng tự mở, nên cả khối
    /// chạy như một đơn vị retriable (cùng cách của <c>IncidentService</c>). Unique violation
    /// trên <c>(ticket_id, ticket_version)</c> dịch thành 409 (BR-CONC-01).
    /// </summary>
    public async Task<T> InTransactionAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        // Consumer MassTransit (inbox/outbox EF) đã mở transaction trên DbContext này: dùng lại nó,
        // không mở lồng và không Clear change tracker (inbox state đang được theo dõi).
        if (_db.Database.CurrentTransaction is not null)
        {
            try
            {
                return await body();
            }
            catch (Exception ex) when (PostgresErrors.IsUniqueViolation(ex))
            {
                throw AppException.Conflict("Ticket vừa được cập nhật bởi một thao tác khác. Tải lại rồi thử lại.",
                    new Dictionary<string, object?> { ["reason"] = "VERSION_CONFLICT" });
            }
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            T result;
            try
            {
                result = await body();
            }
            catch (Exception ex) when (PostgresErrors.IsUniqueViolation(ex))
            {
                throw AppException.Conflict(
                    "Ticket vừa được cập nhật bởi một thao tác khác. Tải lại rồi thử lại.",
                    new Dictionary<string, object?> { ["reason"] = "VERSION_CONFLICT" });
            }
            await tx.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>Khoá hàng ticket trong transaction hiện hành để hai request ghi song song xếp hàng.</summary>
    public Task LockTicketRowAsync(Guid ticketId, CancellationToken ct)
        => _db.Database.ExecuteSqlInterpolatedAsync($"select 1 from tickets where id = {ticketId} for update", ct);

    public Task LockProjectRowAsync(Guid projectId, CancellationToken ct)
        => _db.Database.ExecuteSqlInterpolatedAsync($"select 1 from projects where id = {projectId} for update", ct);

    /// <summary>
    /// Cấp <c>#N</c> mới cho project (mục 5.2): <c>UPDATE ... RETURNING</c> trên chính transaction
    /// hiện hành → hai request tạo ticket song song nhận hai số khác nhau, số không bao giờ tái sử dụng.
    /// </summary>
    public async Task<int> AllocateTicketNumberAsync(Guid projectId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        cmd.CommandText = "update projects set next_ticket_number = next_ticket_number + 1 where id = @id returning next_ticket_number - 1";
        cmd.Parameters.AddWithValue("id", projectId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    // ---------------- Map ----------------

    /// <summary>
    /// Reaction của một người xem trên **nhiều** ticket, gom trong một truy vấn.
    ///
    /// Dùng cho màn hình danh sách. Tra từng dòng thì một trang 25 ticket thành 25 lượt đi DB cho
    /// một thông tin phụ; gom lại là đúng một lượt cho cả trang.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ReactionType>>> ViewerReactionsAsync(
        IReadOnlyCollection<Guid> ticketIds, ClaimsPrincipal viewer, CancellationToken ct)
    {
        if (ticketIds.Count == 0) return new Dictionary<Guid, IReadOnlyList<ReactionType>>();

        var userId = viewer.GetUserId();
        var rows = await _db.TicketReactions.AsNoTracking()
            // `EventId == null` = reaction trên thân ticket; reaction của bình luận đi đường
            // timeline và đã có sẵn trường tương ứng ở đó.
            .Where(r => r.UserId == userId && r.EventId == null && ticketIds.Contains(r.TicketId))
            .Select(r => new { r.TicketId, r.ReactionType })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.TicketId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ReactionType>)g.Select(r => r.ReactionType).ToList());
    }

    /// <param name="viewerReactions">
    /// Truyền vào khi nơi gọi đã nạp sẵn theo lô (màn hình danh sách). Bỏ trống thì mapper tự tra
    /// cho đúng một ticket — hợp lý với màn hình chi tiết, nhưng sẽ là một truy vấn mỗi dòng nếu
    /// dùng trong vòng lặp.
    /// </param>
    public async Task<TicketResponse> ToResponseAsync(Ticket t, ClaimsPrincipal viewer, IReadOnlyList<string>? warnings, CancellationToken ct,
        IReadOnlyList<ReactionType>? viewerReactions = null)
    {
        var slug = t.Project.Slug;
        var html = _renderer.Render(t.Body, slug, await KnownLoginsAsync(t.Body, ct)).Html;

        var subCounts = await _db.Tickets.Where(s => s.ParentTicketId == t.Id)
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Count(), Completed = g.Count(s => s.State == TicketState.Closed) })
            .FirstOrDefaultAsync(ct);
        var total = subCounts?.Total ?? 0;
        var completed = subCounts?.Completed ?? 0;

        TicketRefResponse? duplicateOf = null;
        if (t.DuplicateOfTicketId is { } dupId)
        {
            duplicateOf = await RefAsync(dupId, ct);
        }

        UserSummary? closedBy = null;
        if (t.ClosedById is { } cb)
        {
            var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == cb, ct);
            closedBy = u is null ? null : UserSummary.From(u);
        }

        var association = await AssociationAsync(t.AuthorId, t.ProjectId, t.Id, ct);
        var reactions = ParseReactions(t.ReactionsSummary);
        var mine = viewerReactions ?? (await ViewerReactionsAsync(new[] { t.Id }, viewer, ct)).GetValueOrDefault(t.Id, Array.Empty<ReactionType>());
        var assignees = t.Assignees.OrderBy(a => a.AssignedAt).Select(a => UserSummary.From(a.User)).ToList();

        return new TicketResponse(
            t.Id, slug, t.Number, t.Title, t.Body, html, UserSummary.From(t.Author), association,
            t.State, t.StateReason, duplicateOf, closedBy, t.ClosedAt,
            t.IsLocked, t.ActiveLockReason, t.IsPinned,
            t.Type is null ? null : ToResponse(t.Type),
            t.ParentTicket is null ? null : new TicketRefResponse(t.ParentTicket.Id, t.ParentTicket.Project.Slug, t.ParentTicket.Number, t.ParentTicket.Title, t.ParentTicket.State, t.ParentTicket.StateReason),
            t.Milestone is null ? null : ToResponse(t.Milestone),
            t.Labels.Select(l => ToResponse(l.Label)).OrderBy(l => l.Name).ToList(),
            assignees, assignees.FirstOrDefault(),
            t.Priority, t.SlaDueAt, t.FirstResponseAt,
            t.CommentsCount, reactions, mine,
            new SubIssuesSummary(total, completed, total == 0 ? 0 : (int)Math.Round(completed * 100.0 / total)),
            t.Version, t.CreatedAt, t.UpdatedAt,
            warnings ?? Array.Empty<string>());
    }

    public async Task<TicketRefResponse?> RefAsync(Guid ticketId, CancellationToken ct)
        => await _db.Tickets.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == ticketId)
            .Select(x => new TicketRefResponse(x.Id, x.Project.Slug, x.Number, x.Title, x.State, x.StateReason))
            .FirstOrDefaultAsync(ct);

    public static LabelResponse ToResponse(Label l) => new(l.Id, l.Name, l.ColorHex, l.Description, l.IsDefault, l.IsArchived);

    public static MilestoneResponse ToResponse(Milestone m)
        => new(m.Id, m.Number, m.Title, m.Description, m.DueOn, m.State, m.ClosedAt, m.OpenCount, m.ClosedCount, m.CreatedAt);

    public static IssueTypeResponse ToResponse(IssueType t) => new(t.Id, t.Name, t.Color, t.Description, t.IsEnabled);

    public static IReadOnlyDictionary<string, int> ParseReactions(string? json)
    {
        var dict = new Dictionary<string, int>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return dict;
        }
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.TryGetInt32(out var n))
            {
                dict[p.Name] = n;
            }
        }
        return dict;
    }

    /// <summary>Chỉ link <c>@login</c> của người thật (giống GitHub).</summary>
    public async Task<IReadOnlySet<string>?> KnownLoginsAsync(string? markdown, CancellationToken ct)
    {
        var candidates = MarkdownRenderer.ExtractMentions(markdown);
        if (candidates.Count == 0)
        {
            return new HashSet<string>();
        }
        var found = await _db.Users.AsNoTracking().Where(u => candidates.Contains(u.Login)).Select(u => u.Login).ToListAsync(ct);
        return found.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>BR-SOCIAL-05 — author association của một user trong project (không dùng claims vì user có thể không phải người gọi).</summary>
    public async Task<string> AssociationAsync(Guid userId, Guid projectId, Guid? excludingTicketId, CancellationToken ct)
    {
        var codes = await _db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code))
            .Distinct().ToListAsync(ct);

        var level = codes.Contains(Permissions.TicketDelete) ? AccessLevel.Admin
            : codes.Contains(Permissions.TicketWrite) ? AccessLevel.Write
            : codes.Contains(Permissions.TicketTriage) ? AccessLevel.Triage
            : codes.Contains(Permissions.TicketRead) ? AccessLevel.Read
            : AccessLevel.None;

        var prior = false;
        if (level == AccessLevel.Read)
        {
            prior = await _db.Tickets.IgnoreQueryFilters()
                .AnyAsync(t => t.ProjectId == projectId && t.AuthorId == userId && t.Id != excludingTicketId, ct)
                || await _db.TicketEvents.AsNoTracking()
                    .AnyAsync(e => e.ActorId == userId && e.EventType == TicketEventTypes.Commented
                                   && e.TicketId != excludingTicketId
                                   && _db.Tickets.IgnoreQueryFilters().Any(t => t.Id == e.TicketId && t.ProjectId == projectId), ct);
        }

        return TicketAccess.AssociationOf(level, prior);
    }
}
