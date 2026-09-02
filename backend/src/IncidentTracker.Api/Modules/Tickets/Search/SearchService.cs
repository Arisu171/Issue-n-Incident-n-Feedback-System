using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Search;

public sealed class SearchQuery
{
    public string? Q { get; set; }
    /// <summary>Ghi đè <c>sort:</c> trong q. created-desc (mặc định) | created-asc | updated-desc | updated-asc | comments-desc | reactions-desc | reactions-+1-desc | interactions-desc</summary>
    public string? Sort { get; set; }
    public string? Cursor { get; set; }
    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "per_page")] public int? PerPage { get; set; }
}

/// <summary>UC-14 (Architecture v3.1): tìm kiếm toàn hệ thống trên read model; <c>total_count</c> ≤ 1000.</summary>
public sealed class SearchService
{
    public const int MaxTotal = 1000;

    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly TimeProvider _clock;

    public SearchService(AppDbContext db, TicketQueries q, TimeProvider clock)
    {
        _db = db;
        _q = q;
        _clock = clock;
    }

    public async Task<SearchContext> ContextAsync(ClaimsPrincipal user, IEnumerable<string>? scopes, CancellationToken ct)
    {
        var uid = user.GetUserId();
        var login = await _db.Users.Where(u => u.Id == uid).Select(u => u.Login).FirstAsync(ct);
        return new SearchContext(_db, uid, login, TimelineAccess.CanSeeInternal(user), _clock.GetUtcNow(),
            new HashSet<string>(scopes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Áp DSL lên một truy vấn có sẵn (dùng cho <c>GET /projects/{p}/tickets?q=</c> và board auto-add).</summary>
    public async Task<IQueryable<Ticket>> ApplyAsync(IQueryable<Ticket> source, string dsl, ClaimsPrincipal user, CancellationToken ct)
    {
        var (node, meta) = SearchQueryParser.ExtractMeta(SearchQueryParser.Parse(dsl), "sort", "in");
        var scopes = meta.TryGetValue("in", out var inValue) ? inValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        var ctx = await ContextAsync(user, scopes, ct);
        return source.Where(SearchQueryCompiler.Compile(node, ctx));
    }

    public async Task<CursorPage<TicketResponse>> SearchAsync(SearchQuery query, ClaimsPrincipal user, CancellationToken ct)
    {
        var (node, meta) = SearchQueryParser.ExtractMeta(SearchQueryParser.Parse(query.Q ?? string.Empty), "sort", "in");
        var scopes = meta.TryGetValue("in", out var inValue) ? inValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        var ctx = await ContextAsync(user, scopes, ct);

        var q = _q.WithDetails().Where(t => !t.Project.IsArchived);

        // Cắt theo project ngay trong câu truy vấn, không lọc sau khi lấy về: lọc sau thì phân
        // trang sai (trang 1 còn 3 kết quả, trang 2 còn 0) và dữ liệu vẫn đi qua bộ nhớ tiến
        // trình. Đây là chỗ dễ thủng nhất của cả đợt — Search không cắt thì chỉ cần gõ vào ô tìm
        // kiếm là đọc được tiêu đề và nội dung ticket của project mình không có quyền, và toàn bộ
        // việc cách ly thành vô nghĩa.
        if (user.ProjectsWith(Permissions.TicketRead) is { } slugs)
        {
            q = q.Where(t => slugs.Contains(t.Project.Slug));
        }

        if (TicketAccess.Level(user) < AccessLevel.Triage)
        {
            var uid = ctx.UserId;
            q = q.Where(t => !t.Project.CustomersSeeOnlyOwn || t.AuthorId == uid || t.Assignees.Any(a => a.UserId == uid));
        }
        q = q.Where(SearchQueryCompiler.Compile(node, ctx));

        var sort = (query.Sort ?? (meta.TryGetValue("sort", out var s) ? s : null) ?? "created-desc").ToLowerInvariant();
        q = sort switch
        {
            "created-asc" => q.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id),
            "updated-desc" => q.OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id),
            "updated-asc" => q.OrderBy(t => t.UpdatedAt).ThenBy(t => t.Id),
            "comments-desc" => q.OrderByDescending(t => t.CommentsCount).ThenByDescending(t => t.CreatedAt),
            "comments-asc" => q.OrderBy(t => t.CommentsCount).ThenBy(t => t.CreatedAt),
            "reactions-desc" => q.OrderByDescending(t => _db.TicketReactions.Count(r => r.TicketId == t.Id)).ThenByDescending(t => t.CreatedAt),
            "reactions-+1-desc" => q.OrderByDescending(t => _db.TicketReactions.Count(r => r.TicketId == t.Id && r.ReactionType == ReactionType.ThumbsUp)).ThenByDescending(t => t.CreatedAt),
            "interactions-desc" => q.OrderByDescending(t => t.CommentsCount + _db.TicketReactions.Count(r => r.TicketId == t.Id)).ThenByDescending(t => t.CreatedAt),
            "created-desc" => q.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id),
            _ => throw AppException.BadRequest($"sort '{sort}' không hợp lệ.")
        };

        // Cursor = offset mã hoá (giống Search API của GitHub: page/per_page, tối đa 1000 kết quả).
        var offset = 0;
        var raw = Cursor.DecodeRaw(query.Cursor);
        if (raw is not null && (!int.TryParse(raw, out offset) || offset < 0)) throw AppException.BadRequest("cursor không hợp lệ.");
        var size = Cursor.ClampPageSize(query.PerPage);

        var total = Math.Min(await q.CountAsync(ct), MaxTotal);
        var rows = await q.Skip(offset).Take(Math.Min(size, Math.Max(0, MaxTotal - offset))).ToListAsync(ct);
        var items = new List<TicketResponse>(rows.Count);
        foreach (var t in rows) items.Add(await _q.ToResponseAsync(t, user, null, ct));
        var next = offset + rows.Count < total ? Cursor.Encode((offset + rows.Count).ToString()) : null;
        return new CursorPage<TicketResponse>(items, next, total);
    }
}

[ApiController]
[Route("api/search")]
[Produces("application/json")]
public sealed class SearchController : ControllerBase
{
    private readonly SearchService _search;
    public SearchController(SearchService search) => _search = search;

    /// <summary>≈ <c>GET /search/issues</c>: Query DSL mục 2.7, <c>total_count</c> ≤ 1000.</summary>
    [HttpGet("tickets")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    [ProducesResponseType(typeof(CursorPage<TicketResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<CursorPage<TicketResponse>>> Tickets([FromQuery] SearchQuery query, CancellationToken ct)
        => Ok(await _search.SearchAsync(query, User, ct));
}

/// <summary>
/// Projector (mục 6.1 "Projection Worker"): cập nhật read model text comment cho <c>in:comments</c>,
/// <c>commenter:</c>, <c>mentions:</c>. Rebuild từ Event Store mỗi lần có event liên quan (số comment / ticket nhỏ).
/// </summary>
public sealed class SearchProjector : IConsumer<TicketEventAppended>
{
    private static readonly HashSet<string> Relevant = new(StringComparer.Ordinal)
    {
        TicketEventTypes.Commented, TicketEventTypes.CommentEdited, TicketEventTypes.CommentDeleted, TicketEventTypes.InternalNote, TicketEventTypes.Mentioned
    };

    private readonly AppDbContext _db;
    private readonly TimelineService _timeline;
    private readonly TimeProvider _clock;

    public SearchProjector(AppDbContext db, TimelineService timeline, TimeProvider clock)
    {
        _db = db;
        _timeline = timeline;
        _clock = clock;
    }

    public async Task Consume(ConsumeContext<TicketEventAppended> context)
    {
        var msg = context.Message;
        if (!Relevant.Contains(msg.EventType)) return;
        var ct = context.CancellationToken;

        var bodies = await _timeline.CurrentBodiesAsync(msg.TicketId, ct);
        var actorIds = bodies.Select(b => b.ActorId).Where(a => a != null).Select(a => a!.Value).Distinct().ToList();
        var mentionedIds = await _db.TicketEvents.AsNoTracking().Where(e => e.TicketId == msg.TicketId && e.EventType == TicketEventTypes.Mentioned && e.ActorId != null)
            .Select(e => e.ActorId!.Value).Distinct().ToListAsync(ct);
        var logins = await _db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id) || mentionedIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Login, ct);

        var publicText = string.Join("\n", bodies.Where(b => b.Visibility == EventVisibility.Public).Select(b => b.Body));
        var internalText = string.Join("\n", bodies.Where(b => b.Visibility == EventVisibility.Internal).Select(b => b.Body));
        var commenters = string.Join(" ", bodies.Where(b => b.Visibility == EventVisibility.Public && b.ActorId is not null).Select(b => logins.GetValueOrDefault(b.ActorId!.Value, "")).Distinct());
        var mentioned = string.Join(" ", mentionedIds.Select(id => logins.GetValueOrDefault(id, "")).Distinct());
        var now = _clock.GetUtcNow();

        // Upsert nguyên tử: hai event của cùng ticket xử lý song song không đè nhau.
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            insert into ticket_search_comments (ticket_id, public_text, internal_text, commenters, mentioned, updated_at)
            values ({msg.TicketId}, {publicText}, {internalText}, {commenters}, {mentioned}, {now})
            on conflict (ticket_id) do update set
                public_text = excluded.public_text, internal_text = excluded.internal_text,
                commenters = excluded.commenters, mentioned = excluded.mentioned, updated_at = excluded.updated_at
            """, ct);
    }
}
