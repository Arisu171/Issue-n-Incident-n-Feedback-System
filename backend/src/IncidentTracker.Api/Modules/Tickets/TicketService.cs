using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets;

/// <summary>
/// UC-02/UC-11 (Architecture v3.1): tạo, sửa (`PATCH`), vòng đời (close/reopen/lock/pin/transfer/delete)
/// của ticket. Mọi thay đổi = event append + projection đồng bộ trong một transaction (mục 5).
/// </summary>
public sealed class TicketService
{
    public const int MaxAssignees = 10;
    public const int MaxPinned = 3;

    private readonly TicketQueries _q;
    private readonly AppDbContext _db;
    private readonly TicketEventStore _events;
    private readonly Organization.BoardService _boards;
    private readonly TimeProvider _clock;
    private readonly ILogger<TicketService> _logger;

    /// <summary>Hook cho modify_04/05: template render, sub-issue, mention.</summary>
    public Func<Ticket, CreateTicketRequest, ClaimsPrincipal, CancellationToken, Task>? OnCreating { get; set; }

    public TicketService(TicketQueries q, AppDbContext db, TicketEventStore events, Organization.BoardService boards, TimeProvider clock, ILogger<TicketService> logger)
    {
        _q = q;
        _db = db;
        _events = events;
        _boards = boards;
        _clock = clock;
        _logger = logger;
    }

    // ---------------- Create ----------------

    public async Task<TicketResponse> CreateAsync(string slug, CreateTicketRequest request, ClaimsPrincipal user,
        Func<Project, CreateTicketRequest, ClaimsPrincipal, CancellationToken, Task<(string Title, string Body, TemplateDefaults? Defaults)>>? templateRenderer,
        Func<Ticket, int, ClaimsPrincipal, CancellationToken, Task>? attachParent,
        CancellationToken ct)
    {
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();
        var level = TicketAccess.Level(user);

        var ticketId = await _q.InTransactionAsync(async () =>
        {
            var project = await _q.ProjectAsync(slug, ct);
            if (project.IsArchived)
            {
                throw AppException.Conflict($"Project '{project.Slug}' đã lưu trữ, không nhận ticket mới.");
            }

            var title = request.Title.Trim();
            var body = request.Body ?? string.Empty;
            TemplateDefaults? defaults = null;

            if (request.TemplateId is not null)
            {
                if (templateRenderer is null)
                {
                    throw AppException.BadRequest("Template chưa được hỗ trợ.");
                }
                (title, body, defaults) = await templateRenderer(project, request, user, ct);
            }
            else if (!project.BlankIssuesEnabled && level < AccessLevel.Triage)
            {
                throw AppException.BadRequest("Project này yêu cầu chọn template khi tạo ticket (blank issues đã tắt).");
            }

            await _q.LockProjectRowAsync(project.Id, ct);
            var number = await _q.AllocateTicketNumberAsync(project.Id, ct);

            var ticket = new Ticket
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Project = project,
                Number = number,
                Title = title,
                Body = body,
                AuthorId = actorId,
                State = TicketState.Open,
                CreatedAt = now,
                UpdatedAt = now,
                Version = 0
            };
            _db.Tickets.Add(ticket);

            await _events.AppendAsync(ticket, TicketEventTypes.Opened, new { title, body }, actorId, at: now, ct: ct);

            // Label / milestone / type / priority do NGƯỜI TẠO gửi: chỉ Triage+ (GitHub bỏ qua với mức Read).
            // Giá trị từ defaults của template (BR-TPL-01) là của project nên áp dụng bất kể mức quyền.
            var labels = new List<string>();
            if (level >= AccessLevel.Triage && request.Labels is not null) labels.AddRange(request.Labels);
            if (defaults?.Labels is { Count: > 0 } dl) labels.AddRange(dl);
            foreach (var l in await ResolveLabelsAsync(project.Id, labels.Distinct(StringComparer.OrdinalIgnoreCase), ct))
            {
                await AddLabelAsync(ticket, l, actorId, now, ct);
            }

            var assignees = new List<string>();
            if (request.Assignees is not null) assignees.AddRange(request.Assignees);
            if (assignees.Count > 0)
            {
                var users = await ResolveUsersAsync(assignees, ct);
                if (level < AccessLevel.Triage)
                {
                    users = users.Where(u => u.Id == actorId).ToList(); // Read: chỉ tự assign
                }
                foreach (var u in users.Take(MaxAssignees))
                {
                    await AssignAsync(ticket, u, actorId, now, ct);
                }
            }
            if (defaults?.Assignees is { Count: > 0 } da)
            {
                foreach (var u in await ResolveUsersAsync(da, ct))
                {
                    if (ticket.Assignees.All(a => a.UserId != u.Id) && ticket.Assignees.Count < MaxAssignees)
                        await AssignAsync(ticket, u, actorId, now, ct);
                }
            }

            if (request.Milestone is { } msNumber && level >= AccessLevel.Triage)
            {
                var ms = await _db.Milestones.FirstOrDefaultAsync(m => m.ProjectId == project.Id && m.Number == msNumber, ct)
                         ?? throw AppException.NotFound($"Không tìm thấy milestone #{msNumber}.");
                await SetMilestoneAsync(ticket, ms, actorId, now, ct);
            }

            var typeName = level >= AccessLevel.Triage && !string.IsNullOrWhiteSpace(request.Type) ? request.Type : defaults?.Type;
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                var type = await ResolveTypeAsync(typeName, ct);
                await SetTypeAsync(ticket, type, actorId, now, ct);
            }

            if (request.Priority is { } priority && level >= AccessLevel.Triage)
            {
                await SetPriorityAsync(ticket, priority, actorId, now, ct);
            }

            if (request.ParentNumber is { } parentNumber)
            {
                if (attachParent is null)
                {
                    throw AppException.BadRequest("Sub-issue chưa được hỗ trợ.");
                }
                await attachParent(ticket, parentNumber, user, ct);
            }

            if (defaults?.Boards is { Count: > 0 } boards)
            {
                await _boards.AddToBoardsByNameAsync(ticket, boards, actorId, now, ct);
            }

            if (OnCreating is not null)
            {
                await OnCreating(ticket, request, user, ct);
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Audit ticket.create actor={ActorId} ticket={TicketId} project={Project} number={Number}",
                actorId, ticket.Id, project.Slug, number);
            return ticket.Id;
        }, ct);

        var created = await _q.LoadByIdAsync(ticketId, tracking: false, ct);
        return await _q.ToResponseAsync(created, user, null, ct);
    }

    // ---------------- Read ----------------

    public async Task<TicketResponse> GetAsync(string slug, int number, ClaimsPrincipal user, CancellationToken ct)
    {
        var ticket = await _q.LoadAsync(slug, number, tracking: false, ct);
        TicketAccess.EnsureCanRead(user, ticket, ticket.Project);
        return await _q.ToResponseAsync(ticket, user, null, ct);
    }

    /// <summary>Danh sách cơ bản (cursor theo sort key + id). Query DSL đầy đủ ở modify_07 qua <c>q</c>.</summary>
    public async Task<CursorPage<TicketResponse>> ListAsync(string slug, TicketListQuery query, ClaimsPrincipal user,
        Func<IQueryable<Ticket>, string, Project, ClaimsPrincipal, Task<IQueryable<Ticket>>>? dslFilter, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var q = _q.WithDetails().Where(t => t.ProjectId == project.Id);

        if (project.CustomersSeeOnlyOwn && TicketAccess.Level(user) < AccessLevel.Triage)
        {
            var uid = user.GetUserId();
            q = q.Where(t => t.AuthorId == uid || t.Assignees.Any(a => a.UserId == uid));
        }

        var state = (query.State ?? "open").ToLowerInvariant();
        if (state == "open") q = q.Where(t => t.State == TicketState.Open);
        else if (state == "closed") q = q.Where(t => t.State == TicketState.Closed);

        if (!string.IsNullOrWhiteSpace(query.Labels))
        {
            foreach (var name in query.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var lower = name.ToLowerInvariant();
                q = q.Where(t => t.Labels.Any(l => l.Label.Name.ToLower() == lower));
            }
        }
        if (!string.IsNullOrWhiteSpace(query.Milestone))
        {
            if (query.Milestone == "none") q = q.Where(t => t.MilestoneId == null);
            else if (query.Milestone == "*") q = q.Where(t => t.MilestoneId != null);
            else if (int.TryParse(query.Milestone, out var msn)) q = q.Where(t => t.Milestone != null && t.Milestone.Number == msn);
            else { var mt = query.Milestone.ToLowerInvariant(); q = q.Where(t => t.Milestone != null && t.Milestone.Title.ToLower() == mt); }
        }
        if (!string.IsNullOrWhiteSpace(query.Assignee))
        {
            if (query.Assignee == "none") q = q.Where(t => !t.Assignees.Any());
            else if (query.Assignee == "*") q = q.Where(t => t.Assignees.Any());
            else { var login = query.Assignee == "@me" ? await LoginOf(user, ct) : query.Assignee.ToLowerInvariant(); q = q.Where(t => t.Assignees.Any(a => a.User.Login == login)); }
        }
        if (!string.IsNullOrWhiteSpace(query.Author))
        {
            var login = query.Author == "@me" ? await LoginOf(user, ct) : query.Author.ToLowerInvariant();
            q = q.Where(t => t.Author.Login == login);
        }
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            var tn = query.Type.ToLowerInvariant();
            q = q.Where(t => t.Type != null && t.Type.Name.ToLower() == tn);
        }
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            if (dslFilter is not null)
            {
                q = await dslFilter(q, query.Q, project, user);
            }
            else
            {
                var text = $"%{query.Q.Trim()}%";
                q = q.Where(t => EF.Functions.ILike(t.Title, text) || EF.Functions.ILike(t.Body, text));
            }
        }

        // sort:xxx trong Query DSL (mục 2.7) thắng tham số sort/direction.
        var sortMeta = string.IsNullOrWhiteSpace(query.Q) ? null
            : Search.SearchQueryParser.ExtractMeta(Search.SearchQueryParser.Parse(query.Q), "sort").Meta.GetValueOrDefault("sort");
        if (sortMeta is not null)
        {
            var dash = sortMeta.LastIndexOf('-');
            if (dash > 0)
            {
                query.Sort = sortMeta[..dash].Replace("-+1", string.Empty);
                query.Direction = sortMeta[(dash + 1)..];
            }
        }
        var desc = !string.Equals(query.Direction, "asc", StringComparison.OrdinalIgnoreCase);
        var sort = (query.Sort ?? "created").ToLowerInvariant();
        var perPage = Cursor.ClampPageSize(query.PerPage);

        // Pinned luôn lên đầu (BR-LIFECYCLE-04), sau đó theo sort key rồi id để keyset ổn định.
        var total = await q.CountAsync(ct);
        var (afterKey, afterId) = DecodeListCursor(query.Cursor);

        IOrderedQueryable<Ticket> ordered;
        switch (sort)
        {
            case "updated":
                if (afterKey is not null) q = desc
                    ? q.Where(t => !t.IsPinned && (t.UpdatedAt < afterKey.Value || (t.UpdatedAt == afterKey.Value && t.Id.CompareTo(afterId) < 0)))
                    : q.Where(t => !t.IsPinned && (t.UpdatedAt > afterKey.Value || (t.UpdatedAt == afterKey.Value && t.Id.CompareTo(afterId) > 0)));
                ordered = desc ? q.OrderByDescending(t => t.IsPinned).ThenByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id)
                               : q.OrderByDescending(t => t.IsPinned).ThenBy(t => t.UpdatedAt).ThenBy(t => t.Id);
                break;
            case "comments":
                ordered = desc ? q.OrderByDescending(t => t.IsPinned).ThenByDescending(t => t.CommentsCount).ThenByDescending(t => t.CreatedAt)
                               : q.OrderByDescending(t => t.IsPinned).ThenBy(t => t.CommentsCount).ThenBy(t => t.CreatedAt);
                if (afterKey is not null)
                {
                    // comments không keyset được đơn giản; dùng offset bằng created_at làm khoá phụ.
                    ordered = (IOrderedQueryable<Ticket>)ordered.Where(t => desc ? t.CreatedAt < afterKey.Value : t.CreatedAt > afterKey.Value);
                }
                break;
            default:
                if (afterKey is not null) q = desc
                    ? q.Where(t => !t.IsPinned && (t.CreatedAt < afterKey.Value || (t.CreatedAt == afterKey.Value && t.Id.CompareTo(afterId) < 0)))
                    : q.Where(t => !t.IsPinned && (t.CreatedAt > afterKey.Value || (t.CreatedAt == afterKey.Value && t.Id.CompareTo(afterId) > 0)));
                ordered = desc ? q.OrderByDescending(t => t.IsPinned).ThenByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id)
                               : q.OrderByDescending(t => t.IsPinned).ThenBy(t => t.CreatedAt).ThenBy(t => t.Id);
                break;
        }

        var rows = await ordered.Take(perPage + 1).ToListAsync(ct);
        var hasMore = rows.Count > perPage;
        var page = rows.Take(perPage).ToList();

        // Một truy vấn cho cả trang thay vì một truy vấn mỗi dòng.
        var mine = await _q.ViewerReactionsAsync(page.Select(t => t.Id).ToList(), user, ct);

        var items = new List<TicketResponse>(page.Count);
        foreach (var t in page)
        {
            items.Add(await _q.ToResponseAsync(t, user, null, ct,
                mine.GetValueOrDefault(t.Id, Array.Empty<ReactionType>())));
        }

        string? next = null;
        if (hasMore && page.Count > 0)
        {
            var last = page[^1];
            var key = sort == "updated" ? last.UpdatedAt : last.CreatedAt;
            next = Cursor.Encode($"{key.UtcTicks}|{last.Id}");
        }

        return new CursorPage<TicketResponse>(items, next, total);
    }

    private static (DateTimeOffset? Key, Guid Id) DecodeListCursor(string? cursor)
    {
        var raw = Cursor.DecodeRaw(cursor);
        if (raw is null) return (null, Guid.Empty);
        var parts = raw.Split('|');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParse(parts[1], out var id))
        {
            throw AppException.BadRequest("cursor không hợp lệ.");
        }
        return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
    }

    private async Task<string> LoginOf(ClaimsPrincipal user, CancellationToken ct)
    {
        var uid = user.GetUserId();
        return await _db.Users.Where(u => u.Id == uid).Select(u => u.Login).FirstAsync(ct);
    }

    // ---------------- PATCH ----------------

    public async Task<TicketResponse> UpdateAsync(string slug, int number, UpdateTicketRequest request, ClaimsPrincipal user,
        HttpRequest http, Func<Ticket, CancellationToken, Task<IReadOnlyList<string>>>? closeWarnings, CancellationToken ct)
    {
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();
        var warnings = new List<string>();

        var ticketId = await _q.InTransactionAsync(async () =>
        {
            var probe = await _q.LoadAsync(slug, number, tracking: false, ct);
            await _q.LockTicketRowAsync(probe.Id, ct);
            var ticket = await _q.LoadByIdAsync(probe.Id, tracking: true, ct);
            var project = ticket.Project;

            TicketAccess.EnsureCanRead(user, ticket, project);
            EntityTags.EnsureIfMatch(http, ticket.Version);

            // --- title / body ---
            if (request.Title is not null && request.Title.Trim() != ticket.Title)
            {
                TicketAccess.Ensure(TicketAccess.CanEditContent(user, ticket), "Chỉ tác giả hoặc người có quyền Write mới sửa được tiêu đề.");
                var from = ticket.Title;
                ticket.Title = request.Title.Trim();
                if (ticket.Title.Length == 0) throw AppException.BadRequest("Tiêu đề không được rỗng.");
                await _events.AppendAsync(ticket, TicketEventTypes.Renamed, new { from, to = ticket.Title }, actorId, at: now, ct: ct);
            }

            if (request.Body is not null && request.Body != ticket.Body)
            {
                TicketAccess.Ensure(TicketAccess.CanEditContent(user, ticket), "Chỉ tác giả hoặc người có quyền Write mới sửa được nội dung.");
                var previousHash = MarkdownRenderer.Sha(ticket.Body);
                ticket.Body = request.Body;
                await _events.AppendAsync(ticket, TicketEventTypes.BodyEdited, new { body = ticket.Body, previous_body_hash = previousHash }, actorId, at: now, ct: ct);
            }

            // --- labels (set semantics) ---
            if (request.Labels is not null)
            {
                TicketAccess.Ensure(TicketAccess.CanTriage(user), "Gắn/gỡ label cần quyền Triage.");
                var wanted = await ResolveLabelsAsync(project.Id, request.Labels, ct);
                var wantedIds = wanted.Select(l => l.Id).ToHashSet();
                foreach (var tl in ticket.Labels.Where(l => !wantedIds.Contains(l.LabelId)).ToList())
                {
                    await RemoveLabelAsync(ticket, tl, actorId, now, ct);
                }
                foreach (var l in wanted.Where(l => ticket.Labels.All(tl => tl.LabelId != l.Id)))
                {
                    await AddLabelAsync(ticket, l, actorId, now, ct);
                }
            }

            // --- assignees (set semantics) ---
            if (request.Assignees is not null)
            {
                var users = await ResolveUsersAsync(request.Assignees, ct);
                var wantedIds = users.Select(u => u.Id).ToHashSet();
                var current = ticket.Assignees.Select(a => a.UserId).ToHashSet();
                var changesOthers = wantedIds.Except(current).Concat(current.Except(wantedIds)).Any(id => id != actorId);
                TicketAccess.Ensure(!changesOthers || TicketAccess.CanTriage(user), "Assign người khác cần quyền Triage; mức Read chỉ tự assign/bỏ assign chính mình.");
                if (wantedIds.Count > MaxAssignees)
                {
                    throw new AppException(StatusCodes.Status422UnprocessableEntity, "Quá nhiều assignee", $"Tối đa {MaxAssignees} assignee mỗi ticket (BR-ORG-03).");
                }
                foreach (var a in ticket.Assignees.Where(a => !wantedIds.Contains(a.UserId)).ToList())
                {
                    await UnassignAsync(ticket, a, actorId, now, ct);
                }
                foreach (var u in users.Where(u => !current.Contains(u.Id)))
                {
                    await AssignAsync(ticket, u, actorId, now, ct);
                }
            }

            // --- milestone ---
            if (request.Milestone is { } msNumber)
            {
                TicketAccess.Ensure(TicketAccess.CanTriage(user), "Đổi milestone cần quyền Triage.");
                if (msNumber == 0)
                {
                    if (ticket.MilestoneId is not null) await ClearMilestoneAsync(ticket, actorId, now, ct);
                }
                else
                {
                    var ms = await _db.Milestones.FirstOrDefaultAsync(m => m.ProjectId == project.Id && m.Number == msNumber, ct)
                             ?? throw AppException.NotFound($"Không tìm thấy milestone #{msNumber}.");
                    if (ticket.MilestoneId != ms.Id) await SetMilestoneAsync(ticket, ms, actorId, now, ct);
                }
            }

            // --- type ---
            if (request.Type is not null)
            {
                TicketAccess.Ensure(TicketAccess.CanTriage(user), "Đổi Issue Type cần quyền Triage.");
                if (request.Type.Trim().Length == 0)
                {
                    if (ticket.TypeId is not null) await ClearTypeAsync(ticket, actorId, now, ct);
                }
                else
                {
                    var type = await ResolveTypeAsync(request.Type, ct);
                    if (ticket.TypeId != type.Id) await SetTypeAsync(ticket, type, actorId, now, ct);
                }
            }

            // --- priority (mở rộng) ---
            if (request.ClearPriority == true && ticket.Priority is not null)
            {
                TicketAccess.Ensure(TicketAccess.CanTriage(user), "Đổi priority cần quyền Triage.");
                await SetPriorityAsync(ticket, null, actorId, now, ct);
            }
            else if (request.Priority is { } pr && pr != ticket.Priority)
            {
                TicketAccess.Ensure(TicketAccess.CanTriage(user), "Đổi priority cần quyền Triage.");
                await SetPriorityAsync(ticket, pr, actorId, now, ct);
            }

            // --- state ---
            if (request.State is { } targetState)
            {
                TicketAccess.Ensure(TicketAccess.CanChangeState(user, ticket), "Chỉ tác giả (ticket của mình) hoặc Triage+ mới đóng/mở ticket.");
                if (targetState == TicketState.Closed)
                {
                    if (ticket.State == TicketState.Open)
                    {
                        warnings.AddRange(await CloseAsync(ticket, request.StateReason ?? StateReason.Completed, request.DuplicateOf, actorId, now, closeWarnings, ct));
                    }
                    else if (request.StateReason is { } reason && reason != ticket.StateReason && reason != StateReason.Reopened)
                    {
                        ticket.StateReason = reason; // GitHub cho đổi lý do đóng mà không đổi state.
                    }
                }
                else if (ticket.State == TicketState.Closed)
                {
                    await ReopenAsync(ticket, actorId, now, ct);
                }
            }

            await _db.SaveChangesAsync(ct);
            return ticket.Id;
        }, ct);

        var updated = await _q.LoadByIdAsync(ticketId, tracking: false, ct);
        return await _q.ToResponseAsync(updated, user, warnings, ct);
    }

    // ---------------- Lifecycle building blocks ----------------

    internal async Task<IReadOnlyList<string>> CloseAsync(Ticket ticket, StateReason reason, string? duplicateOf, Guid? actorId,
        DateTimeOffset now, Func<Ticket, CancellationToken, Task<IReadOnlyList<string>>>? closeWarnings, CancellationToken ct,
        string? commitSha = null)
    {
        if (reason == StateReason.Reopened)
        {
            throw AppException.BadRequest("state_reason khi đóng phải là COMPLETED, NOT_PLANNED hoặc DUPLICATE.");
        }

        var warnings = new List<string>();
        var openSubs = await _db.Tickets.CountAsync(t => t.ParentTicketId == ticket.Id && t.State == TicketState.Open, ct);
        if (openSubs > 0) warnings.Add("OPEN_SUB_ISSUES");

        var blockedBy = await (
            from r in _db.TicketReferences
            join t in _db.Tickets on r.TargetId equals t.Id
            where r.SourceId == ticket.Id && r.RelationType == RelationType.BlockedBy && t.State == TicketState.Open
            select new { t.Number, Slug = t.Project.Slug }).ToListAsync(ct);
        if (blockedBy.Count > 0) warnings.Add("BLOCKED_BY_OPEN");

        if (closeWarnings is not null)
        {
            warnings.AddRange(await closeWarnings(ticket, ct));
        }

        // BR-REL-03: mặc định giống GitHub — chỉ cảnh báo. Chặn cứng chỉ khi project bật strict.
        if (ticket.Project.StrictClosePolicy && (openSubs > 0 || blockedBy.Count > 0))
        {
            var blocking = new List<object>();
            var subs = await _db.Tickets.Where(t => t.ParentTicketId == ticket.Id && t.State == TicketState.Open)
                .Select(t => new { kind = "SUB_ISSUE", number = t.Number, project = t.Project.Slug, title = t.Title }).ToListAsync(ct);
            blocking.AddRange(subs);
            blocking.AddRange(blockedBy.Select(b => new { kind = "BLOCKED_BY", number = b.Number, project = b.Slug }));
            throw AppException.Conflict("Ticket còn sub-issue mở hoặc đang bị chặn; project bật strict_close_policy nên không đóng được.",
                new Dictionary<string, object?> { ["blocking_items"] = blocking });
        }

        Guid? duplicateId = null;
        if (reason == StateReason.Duplicate)
        {
            if (string.IsNullOrWhiteSpace(duplicateOf))
            {
                throw AppException.BadRequest("Đóng với lý do DUPLICATE bắt buộc kèm duplicate_of (N hoặc project#N).");
            }
            var target = await _q.ResolveRefAsync(duplicateOf, ticket.Project.Slug, ct);
            if (target.Id == ticket.Id) throw AppException.BadRequest("Ticket không thể trùng với chính nó.");
            duplicateId = target.Id;

            await _events.AppendAsync(ticket, TicketEventTypes.MarkedAsDuplicate,
                new { duplicate_of_ticket_id = target.Id, duplicate_of_number = target.Number, duplicate_of_project = target.Project.Slug }, actorId, at: now, ct: ct);
            _db.TicketReferences.Add(new TicketReference { SourceId = ticket.Id, TargetId = target.Id, RelationType = RelationType.DuplicateOf, CreatedAt = now });
            await _events.AppendAsync(target, TicketEventTypes.CrossReferenced,
                new { source_ticket_id = ticket.Id, source_number = ticket.Number, source_project = ticket.Project.Slug, reason = "DUPLICATE" }, actorId, at: now, ct: ct);
        }

        ticket.State = TicketState.Closed;
        ticket.StateReason = reason;
        ticket.DuplicateOfTicketId = duplicateId;
        ticket.ClosedById = actorId;
        ticket.ClosedAt = now;
        await _events.AppendAsync(ticket, TicketEventTypes.Closed,
            new { state_reason = EnumNaming.Format(reason), duplicate_of_ticket_id = duplicateId, commit_sha = commitSha }, actorId, at: now, ct: ct);

        await AdjustMilestoneCountersAsync(ticket.MilestoneId, openDelta: -1, closedDelta: +1, ct);
        return warnings;
    }

    internal async Task ReopenAsync(Ticket ticket, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        if (ticket.DuplicateOfTicketId is { } dup)
        {
            var reference = await _db.TicketReferences.FirstOrDefaultAsync(r => r.SourceId == ticket.Id && r.TargetId == dup && r.RelationType == RelationType.DuplicateOf, ct);
            if (reference is not null) _db.TicketReferences.Remove(reference);
            await _events.AppendAsync(ticket, TicketEventTypes.UnmarkedAsDuplicate, new { duplicate_of_ticket_id = dup }, actorId, at: now, ct: ct);
        }

        ticket.State = TicketState.Open;
        ticket.StateReason = StateReason.Reopened;
        ticket.DuplicateOfTicketId = null;
        ticket.ClosedById = null;
        ticket.ClosedAt = null;
        await _events.AppendAsync(ticket, TicketEventTypes.Reopened, null, actorId, at: now, ct: ct);
        await AdjustMilestoneCountersAsync(ticket.MilestoneId, openDelta: +1, closedDelta: -1, ct);
    }

    public async Task<TicketResponse> LockAsync(string slug, int number, LockReason? reason, bool locked, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanWrite(user), "Khoá/mở khoá hội thoại cần quyền Write.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        var id = await _q.InTransactionAsync(async () =>
        {
            var probe = await _q.LoadAsync(slug, number, false, ct);
            await _q.LockTicketRowAsync(probe.Id, ct);
            var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);

            if (ticket.IsLocked != locked)
            {
                ticket.IsLocked = locked;
                ticket.ActiveLockReason = locked ? reason : null;
                await _events.AppendAsync(ticket, locked ? TicketEventTypes.Locked : TicketEventTypes.Unlocked,
                    new { lock_reason = reason is null ? null : EnumNaming.Format(reason.Value) }, actorId, at: now, ct: ct);
                await _db.SaveChangesAsync(ct);
            }
            return ticket.Id;
        }, ct);

        return await _q.ToResponseAsync(await _q.LoadByIdAsync(id, false, ct), user, null, ct);
    }

    public async Task<TicketResponse> PinAsync(string slug, int number, bool pinned, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanWrite(user), "Pin ticket cần quyền Write.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        var id = await _q.InTransactionAsync(async () =>
        {
            var probe = await _q.LoadAsync(slug, number, false, ct);
            await _q.LockProjectRowAsync(probe.ProjectId, ct);
            await _q.LockTicketRowAsync(probe.Id, ct);
            var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);

            if (ticket.IsPinned != pinned)
            {
                if (pinned)
                {
                    var count = await _db.Tickets.CountAsync(t => t.ProjectId == ticket.ProjectId && t.IsPinned, ct);
                    if (count >= MaxPinned)
                    {
                        throw new AppException(StatusCodes.Status422UnprocessableEntity, "Đã đủ ticket được pin",
                            $"Mỗi project chỉ pin tối đa {MaxPinned} ticket (BR-LIFECYCLE-04). Bỏ pin một ticket khác trước.");
                    }
                }
                ticket.IsPinned = pinned;
                await _events.AppendAsync(ticket, pinned ? TicketEventTypes.Pinned : TicketEventTypes.Unpinned, null, actorId, at: now, ct: ct);
                await _db.SaveChangesAsync(ct);
            }
            return ticket.Id;
        }, ct);

        return await _q.ToResponseAsync(await _q.LoadByIdAsync(id, false, ct), user, null, ct);
    }

    /// <summary>BR-REL-05 — transfer sang project khác.</summary>
    public async Task<TicketResponse> TransferAsync(string slug, int number, string toSlug, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanWrite(user), "Transfer ticket cần quyền Write ở cả hai project.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        var id = await _q.InTransactionAsync(async () =>
        {
            var probe = await _q.LoadAsync(slug, number, false, ct);
            var target = await _q.ProjectAsync(toSlug, ct);
            if (target.Id == probe.ProjectId) throw AppException.BadRequest("Ticket đã thuộc project này.");
            if (target.IsArchived) throw AppException.Conflict("Project đích đã lưu trữ.");

            await _q.LockTicketRowAsync(probe.Id, ct);
            await _q.LockProjectRowAsync(target.Id, ct);
            var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);
            var fromProject = ticket.Project;
            var fromNumber = ticket.Number;

            var newNumber = await _q.AllocateTicketNumberAsync(target.Id, ct);

            // Label: giữ nếu project đích có label trùng tên; còn lại gỡ (không sinh UNLABELED — giống GitHub).
            var targetLabels = await _db.Labels.Where(l => l.ProjectId == target.Id && !l.IsArchived).ToListAsync(ct);
            foreach (var tl in ticket.Labels.ToList())
            {
                var match = targetLabels.FirstOrDefault(l => string.Equals(l.Name, tl.Label.Name, StringComparison.OrdinalIgnoreCase));
                _db.TicketLabels.Remove(tl);
                if (match is not null)
                {
                    _db.TicketLabels.Add(new TicketLabel { TicketId = ticket.Id, LabelId = match.Id });
                }
            }

            // Milestone: giữ khi trùng title + due_on.
            if (ticket.Milestone is { } ms)
            {
                var match = await _db.Milestones.FirstOrDefaultAsync(m => m.ProjectId == target.Id && m.Title == ms.Title && m.DueOn == ms.DueOn, ct);
                await AdjustMilestoneCountersAsync(ms.Id, ticket.State == TicketState.Open ? -1 : 0, ticket.State == TicketState.Closed ? -1 : 0, ct);
                ticket.MilestoneId = match?.Id;
                ticket.Milestone = match;
                if (match is not null)
                {
                    await AdjustMilestoneCountersAsync(match.Id, ticket.State == TicketState.Open ? 1 : 0, ticket.State == TicketState.Closed ? 1 : 0, ct);
                }
            }

            // Board item bị gỡ; sub-issue cha/con giữ nguyên (GitHub cho phép cross-repo).
            var boardItems = await _db.BoardItems.Where(b => b.TicketId == ticket.Id).ToListAsync(ct);
            _db.BoardItems.RemoveRange(boardItems);

            _db.TicketRedirects.Add(new TicketRedirect { ProjectId = fromProject.Id, Number = fromNumber, TicketId = ticket.Id, CreatedAt = now });
            ticket.ProjectId = target.Id;
            ticket.Project = target;
            ticket.Number = newNumber;

            await _events.AppendAsync(ticket, TicketEventTypes.Transferred,
                new { from_project_id = fromProject.Id, from_project = fromProject.Slug, from_number = fromNumber, to_project_id = target.Id, to_project = target.Slug, to_number = newNumber },
                actorId, at: now, ct: ct);

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Audit ticket.transfer actor={ActorId} ticket={TicketId} from={From}#{FromNumber} to={To}#{ToNumber}",
                actorId, ticket.Id, fromProject.Slug, fromNumber, target.Slug, newNumber);
            return ticket.Id;
        }, ct);

        return await _q.ToResponseAsync(await _q.LoadByIdAsync(id, false, ct), user, null, ct);
    }

    /// <summary>BR-LIFECYCLE-05 — Admin; tombstone, number không cấp lại.</summary>
    public async Task DeleteAsync(string slug, int number, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanDelete(user), "Xoá ticket cần quyền Admin.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        await _q.InTransactionAsync(async () =>
        {
            var probe = await _q.LoadAsync(slug, number, false, ct);
            await _q.LockTicketRowAsync(probe.Id, ct);
            var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);

            await _events.AppendAsync(ticket, TicketEventTypes.Deleted, new { title = ticket.Title }, actorId, at: now, ct: ct);
            ticket.IsDeleted = true;
            if (ticket.State == TicketState.Open)
            {
                await AdjustMilestoneCountersAsync(ticket.MilestoneId, -1, 0, ct);
            }
            else
            {
                await AdjustMilestoneCountersAsync(ticket.MilestoneId, 0, -1, ct);
            }
            var boardItems = await _db.BoardItems.Where(b => b.TicketId == ticket.Id).ToListAsync(ct);
            _db.BoardItems.RemoveRange(boardItems);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Audit ticket.delete actor={ActorId} ticket={TicketId}", actorId, ticket.Id);
            return ticket.Id;
        }, ct);
    }

    // ---------------- Helpers (dùng lại ở modify_04/05) ----------------

    internal async Task<List<Label>> ResolveLabelsAsync(Guid projectId, IEnumerable<string> names, CancellationToken ct)
    {
        var wanted = names.Select(n => n.Trim().ToLowerInvariant()).Where(n => n.Length > 0).Distinct().ToList();
        if (wanted.Count == 0) return new List<Label>();
        var labels = await _db.Labels.Where(l => l.ProjectId == projectId && !l.IsArchived && wanted.Contains(l.Name.ToLower())).ToListAsync(ct);
        var missing = wanted.Except(labels.Select(l => l.Name.ToLowerInvariant())).ToList();
        if (missing.Count > 0)
        {
            throw new AppException(StatusCodes.Status422UnprocessableEntity, "Label không tồn tại",
                $"Không có label: {string.Join(", ", missing)}. Tạo label trước (quyền Write).",
                new Dictionary<string, object?> { ["missing_labels"] = missing });
        }
        return labels;
    }

    /// <summary>
    /// Đổi danh sách login thành user nhận được việc.
    ///
    /// Chỗ duy nhất mọi đường gán đi qua — tạo ticket, sửa ticket, <c>POST /assignees</c>, và
    /// assignee mặc định của template — nên quy tắc chỉ cần đặt ở đây một lần.
    /// </summary>
    internal async Task<List<User>> ResolveUsersAsync(IEnumerable<string> logins, CancellationToken ct)
    {
        var wanted = logins.Select(l => l.Trim().TrimStart('@').ToLowerInvariant()).Where(l => l.Length > 0).Distinct().ToList();
        if (wanted.Count == 0) return new List<User>();
        var users = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .Where(u => wanted.Contains(u.Login) && u.IsActive)
            .ToListAsync(ct);
        var missing = wanted.Except(users.Select(u => u.Login)).ToList();
        if (missing.Count > 0)
        {
            throw new AppException(StatusCodes.Status422UnprocessableEntity, "Assignee không hợp lệ",
                $"Không tìm thấy user đang hoạt động: {string.Join(", ", missing)}.",
                new Dictionary<string, object?> { ["missing_assignees"] = missing });
        }

        // Giao ticket cho khách hàng là vô nghĩa: họ không mở được khu vực phân loại, không đổi
        // được trạng thái, nên cái tên nằm đó chỉ làm người khác tưởng đã có người nhận.
        //
        // `ticket.triage` là đúng ranh giới "người xử lý ticket" (bảng 9.2): admin, support,
        // responder có; customer không. Module Incident đã chặn y hệt từ đầu bằng
        // `incident.update_status` (IncidentService.AssignAsync) — đây chỉ là làm cho module
        // Ticket thống nhất với nó.
        //
        // Quy tắc theo permission chứ không theo tên vai trò, nên vai trò tự tạo mà có
        // `ticket.triage` cũng nhận việc được, và không phải sửa gì ở đây khi thêm vai trò mới.
        var outsiders = users
            .Where(u => !u.UserRoles.SelectMany(ur => ur.Role.RolePermissions)
                .Any(rp => rp.Permission.Code == Permissions.TicketTriage))
            .Select(u => u.Login).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (outsiders.Count > 0)
        {
            throw new AppException(StatusCodes.Status422UnprocessableEntity, "Assignee không hợp lệ",
                $"Không giao ticket được cho: {string.Join(", ", outsiders)}. "
                + $"Người nhận việc phải có quyền '{Permissions.TicketTriage}'.",
                new Dictionary<string, object?> { ["invalid_assignees"] = outsiders });
        }

        return users;
    }

    internal async Task<IssueType> ResolveTypeAsync(string name, CancellationToken ct)
    {
        var lower = name.Trim().ToLowerInvariant();
        return await _db.IssueTypes.FirstOrDefaultAsync(t => t.Name.ToLower() == lower && t.IsEnabled, ct)
               ?? throw new AppException(StatusCodes.Status422UnprocessableEntity, "Issue Type không tồn tại", $"Không có Issue Type '{name}' đang bật.");
    }

    internal async Task AddLabelAsync(Ticket ticket, Label label, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        ticket.Labels.Add(new TicketLabel { TicketId = ticket.Id, LabelId = label.Id, Label = label });
        await _events.AppendAsync(ticket, TicketEventTypes.Labeled, new { label_id = label.Id, label_name = label.Name, color_hex = label.ColorHex }, actorId, at: now, ct: ct);
    }

    internal async Task RemoveLabelAsync(Ticket ticket, TicketLabel tl, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        ticket.Labels.Remove(tl);
        _db.TicketLabels.Remove(tl);
        await _events.AppendAsync(ticket, TicketEventTypes.Unlabeled, new { label_id = tl.LabelId, label_name = tl.Label.Name, color_hex = tl.Label.ColorHex }, actorId, at: now, ct: ct);
    }

    internal async Task AssignAsync(Ticket ticket, User u, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        if (ticket.Assignees.Count >= MaxAssignees)
        {
            throw new AppException(StatusCodes.Status422UnprocessableEntity, "Quá nhiều assignee", $"Tối đa {MaxAssignees} assignee mỗi ticket (BR-ORG-03).");
        }
        ticket.Assignees.Add(new TicketAssignee { TicketId = ticket.Id, UserId = u.Id, User = u, AssignedAt = now });
        await _events.AppendAsync(ticket, TicketEventTypes.Assigned, new { assignee_id = u.Id, login = u.Login, display_name = u.DisplayName }, actorId, at: now, ct: ct);
    }

    internal async Task UnassignAsync(Ticket ticket, TicketAssignee a, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        ticket.Assignees.Remove(a);
        _db.TicketAssignees.Remove(a);
        await _events.AppendAsync(ticket, TicketEventTypes.Unassigned, new { assignee_id = a.UserId, login = a.User.Login, display_name = a.User.DisplayName }, actorId, at: now, ct: ct);
    }

    internal async Task SetMilestoneAsync(Ticket ticket, Milestone ms, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        if (ticket.MilestoneId is not null)
        {
            await ClearMilestoneAsync(ticket, actorId, now, ct);
        }
        ticket.MilestoneId = ms.Id;
        ticket.Milestone = ms;
        await AdjustMilestoneCountersAsync(ms.Id, ticket.State == TicketState.Open ? 1 : 0, ticket.State == TicketState.Closed ? 1 : 0, ct);
        await _events.AppendAsync(ticket, TicketEventTypes.Milestoned, new { milestone_id = ms.Id, number = ms.Number, title = ms.Title }, actorId, at: now, ct: ct);
    }

    internal async Task ClearMilestoneAsync(Ticket ticket, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        var old = ticket.Milestone ?? await _db.Milestones.FirstAsync(m => m.Id == ticket.MilestoneId, ct);
        await AdjustMilestoneCountersAsync(old.Id, ticket.State == TicketState.Open ? -1 : 0, ticket.State == TicketState.Closed ? -1 : 0, ct);
        ticket.MilestoneId = null;
        ticket.Milestone = null;
        await _events.AppendAsync(ticket, TicketEventTypes.Demilestoned, new { milestone_id = old.Id, number = old.Number, title = old.Title }, actorId, at: now, ct: ct);
    }

    internal async Task SetTypeAsync(Ticket ticket, IssueType type, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        if (ticket.TypeId is not null) await ClearTypeAsync(ticket, actorId, now, ct);
        ticket.TypeId = type.Id;
        ticket.Type = type;
        await _events.AppendAsync(ticket, TicketEventTypes.Typed, new { type_id = type.Id, name = type.Name, color = type.Color }, actorId, at: now, ct: ct);
    }

    internal async Task ClearTypeAsync(Ticket ticket, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        var old = ticket.Type ?? await _db.IssueTypes.FirstAsync(t => t.Id == ticket.TypeId, ct);
        ticket.TypeId = null;
        ticket.Type = null;
        await _events.AppendAsync(ticket, TicketEventTypes.Untyped, new { type_id = old.Id, name = old.Name, color = old.Color }, actorId, at: now, ct: ct);
    }

    /// <summary>Mở rộng UC-16: priority → <c>sla_due_at</c> = created_at + response_time của policy.</summary>
    internal async Task SetPriorityAsync(Ticket ticket, TicketPriority? priority, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        var from = ticket.Priority;
        ticket.Priority = priority;
        if (priority is null)
        {
            ticket.SlaDueAt = null;
        }
        else
        {
            var policy = await _db.SlaPolicies.FirstOrDefaultAsync(p => p.Priority == priority && p.IsActive, ct);
            ticket.SlaDueAt = policy is null ? null : ticket.CreatedAt.AddMinutes(policy.ResponseTimeMinutes);
        }
        await _events.AppendAsync(ticket, TicketEventTypes.PriorityChanged,
            new { from = from is null ? null : EnumNaming.Format(from.Value), to = priority is null ? null : EnumNaming.Format(priority.Value), sla_due_at = ticket.SlaDueAt },
            actorId, at: now, ct: ct);
    }

    internal async Task AdjustMilestoneCountersAsync(Guid? milestoneId, int openDelta, int closedDelta, CancellationToken ct)
    {
        if (milestoneId is null || (openDelta == 0 && closedDelta == 0)) return;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"update milestones set open_count = greatest(open_count + {openDelta}, 0), closed_count = greatest(closed_count + {closedDelta}, 0), updated_at = now() where id = {milestoneId}", ct);
    }
}

/// <summary>Giá trị mặc định do template áp lên ticket (UC-17).</summary>
public sealed record TemplateDefaults(List<string> Labels, List<string> Assignees, string? Type, List<string> Boards);
