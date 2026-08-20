using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Modules.Tickets.Realtime;
using IncidentTracker.Api.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Social;

public sealed record NotificationThreadResponse(Guid Id, TicketRefResponse Ticket, NotificationReason Reason, bool Unread, bool IsDone, bool IsSaved,
    string? LastEventType, UserSummary? LastActor, DateTimeOffset? LastReadAt, DateTimeOffset UpdatedAt);

public sealed class NotificationListQuery
{
    /// <summary>true = cả đã đọc.</summary>
    public bool All { get; set; }
    /// <summary>true = chỉ thread có reason mention/assign/author/comment.</summary>
    public bool Participating { get; set; }
    public bool Saved { get; set; }
    public bool Done { get; set; }
    /// <summary>Tìm chữ trong tiêu đề ticket của thông báo.</summary>
    public string? Q { get; set; }
    public string? Cursor { get; set; }
    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "per_page")] public int? PerPage { get; set; }
}

public sealed class NotificationPatchRequest
{
    public bool? Unread { get; set; }
    public bool? Saved { get; set; }
    public bool? Done { get; set; }
}

/// <summary>Kênh email (mục 2.8). Không có SMTP trong phạm vi → cài đặt log-only (sai lệch có chủ đích).</summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken ct);
}

public sealed class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;
    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task SendAsync(string toEmail, string subject, string body, CancellationToken ct)
    {
        _logger.LogInformation("Email (log-only) to={To} subject={Subject}", toEmail, subject);
        return Task.CompletedTask;
    }
}

/// <summary>UC-13 / BR-SOCIAL-03 — inbox gom theo thread (1 dòng / user / ticket).</summary>
public sealed class NotificationService
{
    private readonly AppDbContext _db;
    private readonly IHubContext<TicketHub> _hub;
    private readonly TimeProvider _clock;

    public NotificationService(AppDbContext db, IHubContext<TicketHub> hub, TimeProvider clock)
    {
        _db = db;
        _hub = hub;
        _clock = clock;
    }

    public async Task<CursorPage<NotificationThreadResponse>> ListAsync(NotificationListQuery query, ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var q = _db.NotificationThreads.AsNoTracking().Include(n => n.Ticket).ThenInclude(t => t.Project).Where(n => n.UserId == userId);

        // Lọc **tại lúc đọc**, không phải lúc sinh thông báo: người bị gỡ khỏi một project vẫn
        // còn nguyên các dòng thông báo cũ trỏ vào ticket ở đó, và chúng mang cả tiêu đề ticket.
        q = await FilterVisibleAsync(q, userId, ct);
        if (query.Saved) q = q.Where(n => n.IsSaved);
        else if (query.Done) q = q.Where(n => n.IsDone);
        else
        {
            q = q.Where(n => !n.IsDone);
            if (!query.All) q = q.Where(n => n.Unread);
        }
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            // Lọc trong câu truy vấn, trước khi cắt trang: lọc sau khi lấy về thì con trỏ trang
            // sau nhảy qua đúng những dòng vừa bị loại, và người dùng mất kết quả mà không biết.
            var pattern = "%" + query.Q.Trim() + "%";
            q = q.Where(n => EF.Functions.ILike(n.Ticket.Title, pattern));
        }

        if (query.Participating)
        {
            var participating = new[] { NotificationReason.Mention, NotificationReason.Assign, NotificationReason.Author, NotificationReason.Comment, NotificationReason.TeamMention };
            q = q.Where(n => participating.Contains(n.Reason));
        }

        var size = Cursor.ClampPageSize(query.PerPage);
        var raw = Cursor.DecodeRaw(query.Cursor);
        if (raw is not null)
        {
            var parts = raw.Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], out var ticks) || !Guid.TryParse(parts[1], out var id)) throw AppException.BadRequest("cursor không hợp lệ.");
            var key = new DateTimeOffset(ticks, TimeSpan.Zero);
            q = q.Where(n => n.UpdatedAt < key || (n.UpdatedAt == key && n.Id.CompareTo(id) < 0));
        }

        var rows = await q.OrderByDescending(n => n.UpdatedAt).ThenByDescending(n => n.Id).Take(size + 1).ToListAsync(ct);
        var hasMore = rows.Count > size;
        var page = rows.Take(size).ToList();
        var actorIds = page.Where(n => n.LastActorId != null).Select(n => n.LastActorId!.Value).Distinct().ToList();
        var actors = await _db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var items = page.Select(n => ToResponse(n, actors)).ToList();
        var next = hasMore && page.Count > 0 ? Cursor.Encode($"{page[^1].UpdatedAt.UtcTicks}|{page[^1].Id}") : null;
        var total = await (await UnreadQueryAsync(userId, ct)).CountAsync(ct);
        return new CursorPage<NotificationThreadResponse>(items, next, total);
    }

    public async Task<int> UnreadCountAsync(Guid userId, CancellationToken ct)
        => await (await UnreadQueryAsync(userId, ct)).CountAsync(ct);

    /// <summary>
    /// Thông báo chưa đọc mà người này còn được thấy.
    ///
    /// Phải dùng **cùng một tập** với danh sách: đếm rộng hơn thì huy hiệu báo có 5 cái mới mà mở
    /// ra chỉ thấy 2, và không ai hiểu vì sao.
    /// </summary>
    private async Task<IQueryable<NotificationThread>> UnreadQueryAsync(Guid userId, CancellationToken ct)
        => await FilterVisibleAsync(
            _db.NotificationThreads.AsNoTracking().Where(n => n.UserId == userId && n.Unread && !n.IsDone),
            userId, ct);

    /// <summary>
    /// Cắt danh sách thông báo theo project mà người này còn với tới.
    ///
    /// Lọc theo <c>userId</c> chứ không theo claim: <see cref="PushUnreadAsync"/> chạy từ nền
    /// (SignalR đẩy số chưa đọc) nên không có principal nào để hỏi. Một đường lọc dùng chung cho
    /// cả hai lối vào thì không có chuyện danh sách và con số lệch nhau.
    /// </summary>
    private async Task<IQueryable<NotificationThread>> FilterVisibleAsync(
        IQueryable<NotificationThread> source, Guid userId, CancellationToken ct)
    {
        if (await _db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.Role.IsGlobal, ct))
        {
            return source;
        }

        var accessible = _db.ProjectMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.ProjectId)
            .Union(_db.ProjectRoleAccess
                .Where(a => _db.UserRoles.Any(ur => ur.UserId == userId && ur.RoleId == a.RoleId))
                .Select(a => a.ProjectId));

        return source.Where(n => accessible.Contains(n.Ticket.ProjectId));
    }

    public async Task<NotificationThreadResponse> PatchAsync(Guid threadId, NotificationPatchRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var thread = await _db.NotificationThreads.Include(n => n.Ticket).ThenInclude(t => t.Project).FirstOrDefaultAsync(n => n.Id == threadId && n.UserId == userId, ct)
                     ?? throw AppException.NotFound("Không tìm thấy thông báo.");
        var now = _clock.GetUtcNow();
        if (request.Unread is { } unread)
        {
            thread.Unread = unread;
            if (!unread) thread.LastReadAt = now;
        }
        if (request.Saved is { } saved) thread.IsSaved = saved;
        if (request.Done is { } done) { thread.IsDone = done; if (done) { thread.Unread = false; thread.LastReadAt ??= now; } }
        await _db.SaveChangesAsync(ct);
        await PushUnreadAsync(userId, ct);
        var actor = thread.LastActorId is { } a ? await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == a, ct) : null;
        return ToResponse(thread, actor is null ? new Dictionary<Guid, User>() : new Dictionary<Guid, User> { [actor.Id] = actor });
    }

    /// <summary>Đánh dấu đã đọc thread của ticket (frontend gọi khi mở ticket — giống GitHub).</summary>
    public async Task MarkTicketReadAsync(Guid ticketId, ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = _clock.GetUtcNow();
        await _db.NotificationThreads.Where(n => n.UserId == userId && n.TicketId == ticketId && n.Unread)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Unread, false).SetProperty(n => n.LastReadAt, now), ct);
        await PushUnreadAsync(userId, ct);
    }

    public async Task MarkAllReadAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = _clock.GetUtcNow();
        await _db.NotificationThreads.Where(n => n.UserId == userId && n.Unread)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Unread, false).SetProperty(n => n.LastReadAt, now), ct);
        await PushUnreadAsync(userId, ct);
    }

    public async Task PushUnreadAsync(Guid userId, CancellationToken ct)
    {
        var count = await UnreadCountAsync(userId, ct);
        await _hub.Clients.Group(TicketHub.UserGroup(userId)).SendAsync(TicketHub.NotificationChanged, new { unreadCount = count }, ct);
    }

    private static NotificationThreadResponse ToResponse(NotificationThread n, Dictionary<Guid, User> actors)
        => new(n.Id, new TicketRefResponse(n.Ticket.Id, n.Ticket.Project.Slug, n.Ticket.Number, n.Ticket.Title, n.Ticket.State, n.Ticket.StateReason),
            n.Reason, n.Unread, n.IsDone, n.IsSaved, n.LastEventType,
            n.LastActorId is { } a && actors.TryGetValue(a, out var u) ? UserSummary.From(u) : null, n.LastReadAt, n.UpdatedAt);
}

[ApiController]
[Route("api/notifications")]
[Produces("application/json")]
public sealed class NotificationsController : ControllerBase
{
    private readonly NotificationService _notifications;
    public NotificationsController(NotificationService notifications) => _notifications = notifications;

    [HttpGet]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<CursorPage<NotificationThreadResponse>>> List([FromQuery] NotificationListQuery query, CancellationToken ct)
        => Ok(await _notifications.ListAsync(query, User, ct));

    [HttpGet("unread-count")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<object>> UnreadCount(CancellationToken ct)
        => Ok(new { unreadCount = await _notifications.UnreadCountAsync(User.GetUserId(), ct) });

    [HttpPatch("threads/{id:guid}")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<NotificationThreadResponse>> Patch(Guid id, NotificationPatchRequest request, CancellationToken ct)
        => Ok(await _notifications.PatchAsync(id, request, User, ct));

    /// <summary>Mark as done (≈ <c>DELETE /notifications/threads/{id}</c>).</summary>
    [HttpDelete("threads/{id:guid}")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> Done(Guid id, CancellationToken ct)
    {
        await _notifications.PatchAsync(id, new NotificationPatchRequest { Done = true }, User, ct);
        return NoContent();
    }

    [HttpPost("mark-read")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct)
    {
        await _notifications.MarkAllReadAsync(User, ct);
        return NoContent();
    }

    [HttpPost("tickets/{ticketId:guid}/mark-read")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> MarkTicketRead(Guid ticketId, CancellationToken ct)
    {
        await _notifications.MarkTicketReadAsync(ticketId, User, ct);
        return NoContent();
    }
}

/// <summary>
/// Notification Worker (mục 2.8, 6.1): auto-subscribe theo event, tính người nhận (subscriber SUBSCRIBED ∪
/// watcher ALL − IGNORED − actor), lọc visibility (INTERNAL → chỉ nội bộ; ACTOR_ONLY → chỉ subject), upsert
/// 1 thread / (user, ticket) (BR-SOCIAL-03), đẩy số unread qua SignalR, email log-only.
/// </summary>
public sealed class NotificationWorker : IConsumer<TicketEventAppended>
{
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        TicketEventTypes.Reacted, TicketEventTypes.Unreacted, TicketEventTypes.Subscribed, TicketEventTypes.Unsubscribed,
        TicketEventTypes.BodyEdited, TicketEventTypes.CommentEdited, TicketEventTypes.CommentDeleted, TicketEventTypes.CommentHidden,
        TicketEventTypes.CommentUnhidden, TicketEventTypes.EditHistoryDeleted, TicketEventTypes.FirstResponse, TicketEventTypes.Opened,
        TicketEventTypes.BoardColumnChanged, TicketEventTypes.AddedToBoard, TicketEventTypes.RemovedFromBoard, TicketEventTypes.CrossReferenced
    };

    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly IEmailSender _email;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationWorker> _logger;

    public NotificationWorker(AppDbContext db, NotificationService notifications, IEmailSender email, TimeProvider clock, ILogger<NotificationWorker> logger)
    {
        _db = db;
        _notifications = notifications;
        _email = email;
        _clock = clock;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<TicketEventAppended> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;
        var evt = await _db.TicketEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == msg.EventId, ct);
        if (evt is null) return;
        var ticket = await _db.Tickets.Include(t => t.Project).Include(t => t.Assignees).FirstOrDefaultAsync(t => t.Id == msg.TicketId, ct);
        if (ticket is null) return;
        var payload = TicketEventStore.ParsePayload(evt);

        // Auto-subscribe đã thực hiện đồng bộ trong TicketEventStore.AppendAsync (cùng transaction với event).

        if (Ignored.Contains(evt.EventType)) return;

        // ---- 2. người nhận ----
        var recipients = new Dictionary<Guid, SubscriptionReason>();
        if (evt.Visibility == EventVisibility.ActorOnly)
        {
            if (evt.ActorId is { } subject) recipients[subject] = SubscriptionReason.Mentioned;
        }
        else
        {
            var subs = await _db.TicketSubscriptions.AsNoTracking().Where(s => s.TicketId == ticket.Id).ToListAsync(ct);
            foreach (var s in subs.Where(s => s.State == SubscriptionState.Subscribed)) recipients[s.UserId] = s.Reason;
            var watchers = await _db.ProjectWatches.AsNoTracking().Where(w => w.ProjectId == ticket.ProjectId && w.Level == WatchLevel.All).Select(w => w.UserId).ToListAsync(ct);
            foreach (var w in watchers) recipients.TryAdd(w, SubscriptionReason.Manual);
            foreach (var s in subs.Where(s => s.State != SubscriptionState.Subscribed)) recipients.Remove(s.UserId);
        }
        // UC-16: SLA_WARNING/BREACHED → assignee; ESCALATED → thêm Admin (Lead).
        if (evt.EventType is TicketEventTypes.SlaWarning or TicketEventTypes.SlaBreached or TicketEventTypes.Escalated)
        {
            foreach (var a in ticket.Assignees) recipients.TryAdd(a.UserId, SubscriptionReason.Assigned);
            if (evt.EventType == TicketEventTypes.Escalated)
            {
                var admins = await _db.Users.AsNoTracking().Where(u => u.IsActive && u.UserRoles.Any(r => r.Role.Name == "admin")).Select(u => u.Id).ToListAsync(ct);
                foreach (var id in admins) recipients.TryAdd(id, SubscriptionReason.Manual);
            }
        }
        if (evt.ActorId is { } actor && evt.Visibility != EventVisibility.ActorOnly) recipients.Remove(actor);
        if (recipients.Count == 0) return;

        // ---- 3. lọc quyền (INTERNAL chỉ nội bộ; customers_see_only_own) ----
        var ids = recipients.Keys.ToList();

        // Ai đã tắt thông báo của project này. `WatchLevel.Ignore` trước đây được ghi vào cơ sở
        // dữ liệu nhưng **không nơi nào đọc**: chỗ trên chỉ đọc `Level == All` để *thêm* người
        // nhận, nên tắt đi không gỡ được ai ra. Thông báo vẫn tới qua đăng ký ở mức từng ticket —
        // thứ được tạo tự động khi bạn là tác giả, được giao việc, hay đã bình luận.
        var ignoring = await _db.ProjectWatches.AsNoTracking()
            .Where(w => w.ProjectId == ticket.ProjectId && w.Level == WatchLevel.Ignore && ids.Contains(w.UserId))
            .Select(w => w.UserId)
            .ToListAsync(ct);
        var ignoringSet = ignoring.ToHashSet();
        var users = await _db.Users.AsNoTracking().Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .Where(u => ids.Contains(u.Id) && u.IsActive).ToListAsync(ct);
        var now = _clock.GetUtcNow();
        var touched = new List<Guid>();

        foreach (var u in users)
        {
            var codes = u.UserRoles.SelectMany(r => r.Role.RolePermissions).Select(rp => rp.Permission.Code).ToHashSet();
            if (!codes.Contains(Permissions.TicketRead)) continue;
            if (evt.Visibility == EventVisibility.Internal && !codes.Contains(Permissions.TicketInternalNote)) continue;
            if (ticket.Project.CustomersSeeOnlyOwn && !codes.Contains(Permissions.TicketTriage) && ticket.AuthorId != u.Id && ticket.Assignees.All(a => a.UserId != u.Id)) continue;

            var reason = ReasonFor(evt, u.Id, ticket, recipients[u.Id]);

            // Tắt project thì im, **trừ khi có người gọi thẳng tên mình**. Nuốt luôn cả lời nhắc
            // trực tiếp là cách chắc chắn để người ta bỏ lỡ thứ được gửi đích danh cho họ — và họ
            // sẽ không bao giờ biết là đã bỏ lỡ. Đây cũng là cách GitHub xử lý repo bị ignore.
            if (ignoringSet.Contains(u.Id) && reason != NotificationReason.Mention) continue;
            var lastActor = evt.Visibility == EventVisibility.ActorOnly && payload.TryGetProperty("by", out var by) && by.TryGetGuid(out var byId) ? byId : evt.ActorId;

            // BR-SOCIAL-03: upsert nguyên tử 1 thread / (user, ticket). Hai consumer xử lý song song hai event
            // của cùng ticket không thể đè nhau hay vi phạm unique. Reason: thread đang unread giữ lý do ưu tiên
            // cao hơn (mention > assign > author > comment > state_change > subscribed); đã đọc → lấy lý do mới.
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                insert into notification_threads (id, user_id, ticket_id, reason, unread, is_done, is_saved, last_event_id, last_event_type, last_actor_id, updated_at)
                values ({Guid.NewGuid()}, {u.Id}, {ticket.Id}, {EnumNaming.Format(reason)}, true, false, false, {evt.Id}, {evt.EventType}, {lastActor}, {now})
                on conflict (user_id, ticket_id) do update set
                    reason = case
                        when notification_threads.unread
                             and coalesce(array_position({PriorityNames}::text[], notification_threads.reason), 99) < coalesce(array_position({PriorityNames}::text[], excluded.reason), 99)
                        then notification_threads.reason
                        else excluded.reason end,
                    unread = true,
                    is_done = false,
                    last_event_id = excluded.last_event_id,
                    last_event_type = excluded.last_event_type,
                    last_actor_id = excluded.last_actor_id,
                    updated_at = excluded.updated_at
                """, ct);
            touched.Add(u.Id);

            if (reason is NotificationReason.Mention or NotificationReason.Assign or NotificationReason.SlaBreach)
            {
                await _email.SendAsync(u.Email, $"[{ticket.Project.Slug}#{ticket.Number}] {ticket.Title}", $"{evt.EventType} — {EnumNaming.Format(reason)}", ct);
            }
        }

        foreach (var userId in touched.Distinct())
        {
            await _notifications.PushUnreadAsync(userId, ct);
        }
        _logger.LogInformation("NotificationWorker: event={EventId} type={Type} recipients={Count}", evt.Id, evt.EventType, touched.Count);
    }

    private static NotificationReason ReasonFor(TicketEvent evt, Guid userId, Ticket ticket, SubscriptionReason subscription)
    {
        if (evt.EventType is TicketEventTypes.SlaWarning or TicketEventTypes.SlaBreached or TicketEventTypes.Escalated) return NotificationReason.SlaBreach;
        if (evt.EventType == TicketEventTypes.Mentioned) return NotificationReason.Mention;
        if (evt.EventType == TicketEventTypes.Assigned && TicketEventStore.ParsePayload(evt).TryGetProperty("assignee_id", out var a) && a.TryGetGuid(out var aid) && aid == userId) return NotificationReason.Assign;
        return subscription switch
        {
            SubscriptionReason.Author => NotificationReason.Author,
            SubscriptionReason.Assigned => NotificationReason.Assign,
            SubscriptionReason.Commented => NotificationReason.Comment,
            SubscriptionReason.Mentioned => NotificationReason.Mention,
            SubscriptionReason.StateChange => NotificationReason.StateChange,
            SubscriptionReason.TeamMention => NotificationReason.TeamMention,
            SubscriptionReason.Manual => ticket.AuthorId == userId ? NotificationReason.Author : NotificationReason.Subscribed,
            _ => NotificationReason.Subscribed
        };
    }

    // Ưu tiên (mục 2.8): mention > assign > author > comment > state_change > subscribed.
    private static readonly NotificationReason[] Priority =
    {
        NotificationReason.SlaBreach, NotificationReason.Mention, NotificationReason.TeamMention, NotificationReason.Assign,
        NotificationReason.Author, NotificationReason.Comment, NotificationReason.StateChange, NotificationReason.Manual, NotificationReason.Subscribed
    };

    private static readonly string[] PriorityNames = Priority.Select(EnumNaming.Format).ToArray();
}
