using System.Security.Claims;
using System.Text.Json;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets;

/// <summary>
/// UC-02 (comment), UC-03 (internal note), UC-18 (sửa/xoá/ẩn comment) — Architecture v3.1.
/// Comment là event <c>COMMENTED</c>; mọi thay đổi sau đó là event bù trừ trỏ tới nó bằng
/// <c>target_event_id</c> (BR-EV-01). "Trạng thái hiện tại" của một comment được
/// <see cref="TimelineService"/> tính lại từ chuỗi event.
/// </summary>
public sealed class CommentService
{
    private readonly TicketQueries _q;
    private readonly AppDbContext _db;
    private readonly TicketEventStore _events;
    private readonly TicketService _tickets;
    private readonly TimelineService _timeline;
    private readonly TimeProvider _clock;
    private readonly ILogger<CommentService> _logger;

    public CommentService(TicketQueries q, AppDbContext db, TicketEventStore events, TicketService tickets,
        TimelineService timeline, TimeProvider clock, ILogger<CommentService> logger)
    {
        _q = q;
        _db = db;
        _events = events;
        _tickets = tickets;
        _timeline = timeline;
        _clock = clock;
        _logger = logger;
    }

    public async Task<CursorPage<CommentResponse>> ListAsync(string slug, int number, string? cursor, int? perPage,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var ticket = await _q.LoadAsync(slug, number, false, ct);
        TicketAccess.EnsureCanRead(user, ticket, ticket.Project);
        return await _timeline.CommentsAsync(ticket, cursor, perPage, user, ct);
    }

    public async Task<CommentResponse> CreateAsync(string slug, int number, CommentRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        var eventId = await _q.InTransactionAsync(async () =>
        {
            var probe = await _q.LoadAsync(slug, number, false, ct);
            await _q.LockTicketRowAsync(probe.Id, ct);
            var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);
            TicketAccess.EnsureCanRead(user, ticket, ticket.Project);

            if (!user.HasPermission(Permissions.TicketComment))
            {
                throw AppException.Forbidden("Bạn không có quyền bình luận.");
            }
            if (ticket.IsLocked && !TicketAccess.CanWrite(user))
            {
                throw AppException.Forbidden("Hội thoại đã bị khoá; chỉ người có quyền Write mới bình luận được (BR-LIFECYCLE-02).");
            }

            var evt = await _events.AppendAsync(ticket, TicketEventTypes.Commented, new { body = request.Body }, actorId, at: now, ct: ct);
            ticket.CommentsCount += 1;

            // Mở rộng UC-16: phản hồi đầu tiên của nhân viên trên ticket của khách dừng đồng hồ SLA.
            var level = TicketAccess.Level(user);
            if (ticket.FirstResponseAt is null && level >= AccessLevel.Triage && ticket.AuthorId != actorId)
            {
                var authorLevel = await LevelOfUserAsync(ticket.AuthorId, ct);
                if (authorLevel <= AccessLevel.Read)
                {
                    ticket.FirstResponseAt = now;
                    await _events.AppendAsync(ticket, TicketEventTypes.FirstResponse, new { response_event_id = evt.Id }, actorId, at: now, ct: ct);
                }
            }

            // BR-LIFECYCLE-03: comment vào ticket đã đóng KHÔNG mở lại (giống GitHub) — trừ khi project
            // bật cờ mở rộng và người comment là khách (mức Read).
            if (ticket.State == TicketState.Closed && ticket.Project.AutoReopenOnCustomerComment && level == AccessLevel.Read)
            {
                await _tickets.ReopenAsync(ticket, null, now, ct);
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Audit ticket.comment actor={ActorId} ticket={TicketId} event={EventId}", actorId, ticket.Id, evt.Id);
            return evt.Id;
        }, ct);

        return await _timeline.CommentAsync(eventId, user, ct);
    }

    public async Task<CommentResponse> InternalNoteAsync(string slug, int number, CommentRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(user.HasPermission(Permissions.TicketInternalNote), "Ghi chú nội bộ cần quyền ticket.internal_note.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        var eventId = await _q.InTransactionAsync(async () =>
        {
            var probe = await _q.LoadAsync(slug, number, false, ct);
            await _q.LockTicketRowAsync(probe.Id, ct);
            var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);
            var evt = await _events.AppendAsync(ticket, TicketEventTypes.InternalNote, new { body = request.Body }, actorId, EventVisibility.Internal, now, ct);
            await _db.SaveChangesAsync(ct);
            return evt.Id;
        }, ct);

        return await _timeline.CommentAsync(eventId, user, ct);
    }

    public async Task<CommentResponse> EditAsync(string slug, int number, Guid commentId, CommentRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        await _q.InTransactionAsync(async () =>
        {
            var (ticket, comment) = await LoadCommentAsync(slug, number, commentId, user, ct);
            TicketAccess.Ensure(TicketAccess.CanModerateComment(user, comment.ActorId), "Chỉ tác giả comment hoặc Write+ mới sửa được.");
            if (ticket.IsLocked && !TicketAccess.CanWrite(user))
            {
                throw AppException.Forbidden("Hội thoại đã bị khoá.");
            }

            var currentBody = await _timeline.CurrentBodyAsync(comment, ct);
            if (currentBody == request.Body)
            {
                return true;
            }

            await _events.AppendAsync(ticket, TicketEventTypes.CommentEdited,
                new { target_event_id = comment.Id, body = request.Body, previous_body_hash = MarkdownRenderer.Sha(currentBody) },
                actorId, comment.Visibility, now, ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return await _timeline.CommentAsync(commentId, user, ct);
    }

    public async Task DeleteAsync(string slug, int number, Guid commentId, ClaimsPrincipal user, CancellationToken ct)
    {
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        await _q.InTransactionAsync(async () =>
        {
            var (ticket, comment) = await LoadCommentAsync(slug, number, commentId, user, ct);
            TicketAccess.Ensure(TicketAccess.CanModerateComment(user, comment.ActorId), "Chỉ tác giả comment hoặc Write+ mới xoá được.");

            await _events.AppendAsync(ticket, TicketEventTypes.CommentDeleted, new { target_event_id = comment.Id }, actorId, comment.Visibility, now, ct);
            if (comment.EventType == TicketEventTypes.Commented && ticket.CommentsCount > 0)
            {
                ticket.CommentsCount -= 1;
            }
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    public async Task<CommentResponse> MinimizeAsync(string slug, int number, Guid commentId, HideReason? reason, bool hide, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanWrite(user), "Ẩn/bỏ ẩn comment cần quyền Write.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        await _q.InTransactionAsync(async () =>
        {
            var (ticket, comment) = await LoadCommentAsync(slug, number, commentId, user, ct);
            await _events.AppendAsync(ticket, hide ? TicketEventTypes.CommentHidden : TicketEventTypes.CommentUnhidden,
                new { target_event_id = comment.Id, reason = reason is null ? null : EnumNaming.Format(reason.Value) },
                actorId, comment.Visibility, now, ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return await _timeline.CommentAsync(commentId, user, ct);
    }

    public async Task<IReadOnlyList<CommentEditResponse>> EditsAsync(string slug, int number, Guid commentId, ClaimsPrincipal user, CancellationToken ct)
    {
        var (_, comment) = await LoadCommentAsync(slug, number, commentId, user, ct);
        return await _timeline.EditsAsync(comment, ct);
    }

    /// <summary>BR-EDIT-01: Write+ xoá một bản sửa khỏi lịch sử hiển thị.</summary>
    public async Task DeleteEditAsync(string slug, int number, Guid commentId, Guid revisionEventId, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanWrite(user), "Xoá bản sửa cần quyền Write.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        await _q.InTransactionAsync(async () =>
        {
            var (ticket, comment) = await LoadCommentAsync(slug, number, commentId, user, ct);
            var revision = await _db.TicketEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == revisionEventId && e.TicketId == ticket.Id, ct)
                ?? throw AppException.NotFound("Không tìm thấy bản sửa.");
            await _events.AppendAsync(ticket, TicketEventTypes.EditHistoryDeleted,
                new { target_event_id = comment.Id, revision_event_id = revision.Id }, actorId, comment.Visibility, now, ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    // ---------------- helpers ----------------

    private async Task<(Ticket Ticket, TicketEvent Comment)> LoadCommentAsync(string slug, int number, Guid commentId, ClaimsPrincipal user, CancellationToken ct)
    {
        var probe = await _q.LoadAsync(slug, number, false, ct);
        await _q.LockTicketRowAsync(probe.Id, ct);
        var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);
        TicketAccess.EnsureCanRead(user, ticket, ticket.Project);

        var comment = await _db.TicketEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == commentId && e.TicketId == ticket.Id
                                      && (e.EventType == TicketEventTypes.Commented || e.EventType == TicketEventTypes.InternalNote), ct)
            ?? throw AppException.NotFound("Không tìm thấy comment.");

        if (!TimelineAccess.CanSee(user, comment))
        {
            throw AppException.NotFound("Không tìm thấy comment.");
        }
        if (await _timeline.IsDeletedAsync(comment, ct))
        {
            throw AppException.NotFound("Comment đã bị xoá.");
        }
        return (ticket, comment);
    }

    private async Task<AccessLevel> LevelOfUserAsync(Guid userId, CancellationToken ct)
    {
        var codes = await _db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code)).Distinct().ToListAsync(ct);
        return codes.Contains(Permissions.TicketDelete) ? AccessLevel.Admin
            : codes.Contains(Permissions.TicketWrite) ? AccessLevel.Write
            : codes.Contains(Permissions.TicketTriage) ? AccessLevel.Triage
            : codes.Contains(Permissions.TicketRead) ? AccessLevel.Read : AccessLevel.None;
    }
}
