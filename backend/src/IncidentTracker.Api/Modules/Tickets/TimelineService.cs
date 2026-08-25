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
/// Đọc timeline (UC-05, mục 6.5 <c>GET .../timeline</c>): keyset theo <c>sequence</c> (BR-SCALE-01),
/// lọc visibility (BR-SEC-06), gộp event bù trừ (sửa/xoá/ẩn) thành trạng thái hiện tại của comment,
/// ẩn event "silent" (reaction, subscribe, body edit...) giống GitHub.
/// </summary>
public sealed class TimelineService
{
    private static readonly string[] CompensationTypes =
    {
        TicketEventTypes.CommentEdited, TicketEventTypes.CommentDeleted,
        TicketEventTypes.CommentHidden, TicketEventTypes.CommentUnhidden, TicketEventTypes.EditHistoryDeleted
    };

    private readonly AppDbContext _db;
    private readonly MarkdownRenderer _renderer;
    private readonly TicketQueries _q;

    public TimelineService(AppDbContext db, MarkdownRenderer renderer, TicketQueries q)
    {
        _db = db;
        _renderer = renderer;
        _q = q;
    }

    /// <summary>Trạng thái hiện tại của một comment (suy từ chuỗi event bù trừ).</summary>
    public sealed record CommentState(bool Deleted, bool Hidden, HideReason? HiddenReason, string? CurrentBody, DateTimeOffset? EditedAt, Guid? EditorId);

    public async Task<CursorPage<TimelineEventDto>> ListAsync(Ticket ticket, string? cursor, int? perPage, ClaimsPrincipal user, CancellationToken ct)
    {
        var after = Cursor.DecodeSequence(cursor);
        var size = Cursor.ClampPageSize(perPage);

        var q = _db.TicketEvents.AsNoTracking().Where(e => e.TicketId == ticket.Id);
        q = TimelineAccess.ApplyVisibility(q, user);
        if (after is not null)
        {
            q = q.Where(e => e.Sequence > after.Value);
        }

        // Event silent không hiện thành dòng — cùng danh sách mà bộ phát real-time dùng.
        var silent = TicketEventTypes.Silent;
        q = q.Where(e => !silent.Contains(e.EventType));

        var rows = await q.OrderBy(e => e.Sequence).Take(size + 1).ToListAsync(ct);
        var hasMore = rows.Count > size;
        var page = rows.Take(size).ToList();

        var states = await CommentStatesAsync(ticket.Id, page.Where(e => TicketEventTypes.HasBody(e.EventType)).Select(e => e.Id).ToList(), ct);
        var visible = page.Where(e => !states.TryGetValue(e.Id, out var st) || !st.Deleted).ToList();

        var dtos = await MapAsync(ticket, visible, states, user, ct);
        var next = hasMore && page.Count > 0 ? Cursor.Encode(page[^1].Sequence) : null;
        return new CursorPage<TimelineEventDto>(dtos, next);
    }

    public async Task<CursorPage<CommentResponse>> CommentsAsync(Ticket ticket, string? cursor, int? perPage, ClaimsPrincipal user, CancellationToken ct)
    {
        var after = Cursor.DecodeSequence(cursor);
        var size = Cursor.ClampPageSize(perPage);

        var q = _db.TicketEvents.AsNoTracking()
            .Where(e => e.TicketId == ticket.Id && e.EventType == TicketEventTypes.Commented);
        q = TimelineAccess.ApplyVisibility(q, user);
        if (after is not null) q = q.Where(e => e.Sequence > after.Value);

        var rows = await q.OrderBy(e => e.Sequence).Take(size + 1).ToListAsync(ct);
        var hasMore = rows.Count > size;
        var page = rows.Take(size).ToList();
        var states = await CommentStatesAsync(ticket.Id, page.Select(e => e.Id).ToList(), ct);
        var visible = page.Where(e => !states.TryGetValue(e.Id, out var st) || !st.Deleted).ToList();

        var dtos = await MapAsync(ticket, visible, states, user, ct);
        var items = dtos.Select(ToComment).ToList();
        var next = hasMore && page.Count > 0 ? Cursor.Encode(page[^1].Sequence) : null;
        return new CursorPage<CommentResponse>(items, next);
    }

    public async Task<CommentResponse> CommentAsync(Guid eventId, ClaimsPrincipal user, CancellationToken ct)
    {
        var evt = await _db.TicketEvents.AsNoTracking().FirstAsync(e => e.Id == eventId, ct);
        var ticket = await _q.LoadByIdAsync(evt.TicketId, false, ct);
        var states = await CommentStatesAsync(ticket.Id, new[] { evt.Id }, ct);
        var dto = (await MapAsync(ticket, new[] { evt }, states, user, ct)).Single();
        return ToComment(dto);
    }

    public async Task<string> CurrentBodyAsync(TicketEvent comment, CancellationToken ct)
    {
        var states = await CommentStatesAsync(comment.TicketId, new[] { comment.Id }, ct);
        if (states.TryGetValue(comment.Id, out var st) && st.CurrentBody is not null)
        {
            return st.CurrentBody;
        }
        var payload = TicketEventStore.ParsePayload(comment);
        return payload.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;
    }

    public async Task<bool> IsDeletedAsync(TicketEvent comment, CancellationToken ct)
    {
        var states = await CommentStatesAsync(comment.TicketId, new[] { comment.Id }, ct);
        return states.TryGetValue(comment.Id, out var st) && st.Deleted;
    }

    public async Task<IReadOnlyList<CommentEditResponse>> EditsAsync(TicketEvent comment, CancellationToken ct)
    {
        var target = comment.Id.ToString();
        var comps = (await _db.TicketEvents.AsNoTracking()
                .Where(e => e.TicketId == comment.TicketId && CompensationTypes.Contains(e.EventType))
                .OrderBy(e => e.Sequence).ToListAsync(ct))
            .Where(e => e.Payload.Contains(target, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var removedRevisions = comps.Where(e => e.EventType == TicketEventTypes.EditHistoryDeleted)
            .Select(e => TicketEventStore.ParsePayload(e).GetProperty("revision_event_id").GetGuid()).ToHashSet();

        var editorIds = comps.Select(e => e.ActorId).Append(comment.ActorId).Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        var users = await _db.Users.AsNoTracking().Where(u => editorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        var list = new List<CommentEditResponse>();
        var original = TicketEventStore.ParsePayload(comment).GetProperty("body").GetString() ?? string.Empty;
        list.Add(new CommentEditResponse(comment.Id, Summary(users, comment.ActorId), original, comment.CreatedAt, false));

        foreach (var e in comps.Where(e => e.EventType == TicketEventTypes.CommentEdited && !removedRevisions.Contains(e.Id)))
        {
            var body = TicketEventStore.ParsePayload(e).GetProperty("body").GetString() ?? string.Empty;
            list.Add(new CommentEditResponse(e.Id, Summary(users, e.ActorId), body, e.CreatedAt, false));
        }

        if (list.Count > 0)
        {
            list[^1] = list[^1] with { IsCurrent = true };
        }
        return list;
    }

    /// <summary>Thân hiện hành của mọi comment/internal note chưa bị xoá (cho SearchProjector).</summary>
    public sealed record CurrentBody(Guid EventId, Guid? ActorId, EventVisibility Visibility, string Body);

    public async Task<IReadOnlyList<CurrentBody>> CurrentBodiesAsync(Guid ticketId, CancellationToken ct)
    {
        var types = new[] { TicketEventTypes.Commented, TicketEventTypes.InternalNote };
        var comments = await _db.TicketEvents.AsNoTracking().Where(e => e.TicketId == ticketId && types.Contains(e.EventType)).OrderBy(e => e.Sequence).ToListAsync(ct);
        var states = await CommentStatesAsync(ticketId, comments.Select(c => c.Id).ToList(), ct);
        var list = new List<CurrentBody>();
        foreach (var c in comments)
        {
            states.TryGetValue(c.Id, out var st);
            if (st?.Deleted == true) continue;
            var body = st?.CurrentBody ?? (TicketEventStore.ParsePayload(c).TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty);
            list.Add(new CurrentBody(c.Id, c.ActorId, c.Visibility, body));
        }
        return list;
    }

    // ---------------- internals ----------------

    private async Task<Dictionary<Guid, CommentState>> CommentStatesAsync(Guid ticketId, IReadOnlyCollection<Guid> commentIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, CommentState>();
        if (commentIds.Count == 0) return result;

        // Event bù trừ của một ticket thường ít; nạp hết rồi gộp trong bộ nhớ.
        var comps = await _db.TicketEvents.AsNoTracking()
            .Where(e => e.TicketId == ticketId && CompensationTypes.Contains(e.EventType))
            .OrderBy(e => e.Sequence).ToListAsync(ct);

        var wanted = commentIds.ToHashSet();
        var removedRevisions = comps.Where(e => e.EventType == TicketEventTypes.EditHistoryDeleted)
            .Select(e => TicketEventStore.ParsePayload(e).GetProperty("revision_event_id").GetGuid()).ToHashSet();

        foreach (var e in comps)
        {
            var payload = TicketEventStore.ParsePayload(e);
            if (!payload.TryGetProperty("target_event_id", out var t) || !t.TryGetGuid(out var target) || !wanted.Contains(target))
            {
                continue;
            }

            result.TryGetValue(target, out var st);
            st ??= new CommentState(false, false, null, null, null, null);

            st = e.EventType switch
            {
                TicketEventTypes.CommentDeleted => st with { Deleted = true },
                TicketEventTypes.CommentHidden => st with { Hidden = true, HiddenReason = payload.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String && EnumNaming.TryParse<HideReason>(r.GetString(), out var hr) ? hr : null },
                TicketEventTypes.CommentUnhidden => st with { Hidden = false, HiddenReason = null },
                TicketEventTypes.CommentEdited when !removedRevisions.Contains(e.Id)
                    => st with { CurrentBody = payload.GetProperty("body").GetString(), EditedAt = e.CreatedAt, EditorId = e.ActorId },
                _ => st
            };
            result[target] = st;
        }

        return result;
    }

    private async Task<IReadOnlyList<TimelineEventDto>> MapAsync(Ticket ticket, IReadOnlyList<TicketEvent> events,
        Dictionary<Guid, CommentState> states, ClaimsPrincipal user, CancellationToken ct)
    {
        if (events.Count == 0) return Array.Empty<TimelineEventDto>();

        var actorIds = events.Where(e => e.ActorId is not null).Select(e => e.ActorId!.Value).Distinct().ToList();
        var users = await _db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        var commentIds = events.Where(e => TicketEventTypes.HasBody(e.EventType)).Select(e => e.Id).ToList();
        var viewerId = user.GetUserId();
        var reactions = commentIds.Count == 0
            ? new List<TicketReaction>()
            : await _db.TicketReactions.AsNoTracking().Where(r => r.TicketId == ticket.Id && r.EventId != null && commentIds.Contains(r.EventId.Value)).ToListAsync(ct);

        // Login thật để link @mention.
        var allBodies = string.Join("\n", events.Select(e => e.Payload));
        var knownLogins = await _q.KnownLoginsAsync(allBodies, ct);

        var associations = new Dictionary<Guid, string>();
        var list = new List<TimelineEventDto>(events.Count);
        foreach (var e in events)
        {
            var actor = e.ActorId is { } a && users.TryGetValue(a, out var u) ? UserSummary.From(u) : null;
            var dto = TimelineMapper.ToDto(e, actor, _renderer, ticket.Project.Slug, knownLogins);

            if (TicketEventTypes.HasBody(e.EventType))
            {
                states.TryGetValue(e.Id, out var st);
                var body = st?.CurrentBody ?? dto.Body ?? string.Empty;
                var html = _renderer.Render(body, ticket.Project.Slug, knownLogins).Html;
                var mine = reactions.Where(r => r.EventId == e.Id);
                var counts = mine.GroupBy(r => EnumNaming.Format(r.ReactionType)).ToDictionary(g => g.Key, g => g.Count());
                var viewer = mine.Where(r => r.UserId == viewerId).Select(r => r.ReactionType).ToList();

                string? association = null;
                if (e.ActorId is { } authorId)
                {
                    if (!associations.TryGetValue(authorId, out association))
                    {
                        association = await _q.AssociationAsync(authorId, ticket.ProjectId, null, ct);
                        associations[authorId] = association;
                    }
                }

                dto = dto with
                {
                    Body = body,
                    BodyHtml = html,
                    IsEdited = st?.EditedAt is not null,
                    EditedAt = st?.EditedAt,
                    IsHidden = st?.Hidden ?? false,
                    HiddenReason = st?.HiddenReason,
                    Reactions = counts,
                    ViewerReactions = viewer,
                    AuthorAssociation = association
                };
            }

            list.Add(dto);
        }
        return list;
    }

    private static CommentResponse ToComment(TimelineEventDto d)
        => new(d.Id, d.Sequence, d.TicketId, d.Actor, d.AuthorAssociation ?? "NONE", d.Body ?? string.Empty, d.BodyHtml ?? string.Empty,
            d.CreatedAt, d.EditedAt, d.IsHidden, d.HiddenReason, d.Reactions ?? new Dictionary<string, int>(), d.ViewerReactions ?? Array.Empty<ReactionType>());

    private static UserSummary? Summary(Dictionary<Guid, User> users, Guid? id)
        => id is { } i && users.TryGetValue(i, out var u) ? UserSummary.From(u) : null;
}
