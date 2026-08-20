using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Social;

public sealed class ReactionRequest
{
    /// <summary>THUMBS_UP / THUMBS_DOWN / LAUGH / HOORAY / CONFUSED / HEART / ROCKET / EYES (hoặc +1 / -1 như GitHub).</summary>
    [Required] public string Content { get; set; } = string.Empty;
}

public sealed record ReactionSummaryResponse(IReadOnlyDictionary<string, int> Counts, IReadOnlyList<ReactionType> ViewerReactions, int Total);

/// <summary>UC-12 / BR-SOCIAL-01 (Architecture v3.1).</summary>
public sealed class ReactionService
{
    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly TicketEventStore _events;
    private readonly TimeProvider _clock;

    public ReactionService(AppDbContext db, TicketQueries q, TicketEventStore events, TimeProvider clock)
    {
        _db = db;
        _q = q;
        _events = events;
        _clock = clock;
    }

    public static ReactionType Parse(string content)
    {
        var text = content.Trim();
        if (text == "+1") return ReactionType.ThumbsUp;
        if (text == "-1") return ReactionType.ThumbsDown;
        return EnumNaming.TryParse<ReactionType>(text, out var r)
            ? r
            : throw AppException.BadRequest($"content '{content}' không hợp lệ. Chấp nhận: {string.Join(", ", EnumNaming.Names<ReactionType>())}, +1, -1.");
    }

    public async Task<ReactionSummaryResponse> SummaryAsync(string slug, int number, Guid? eventId, ClaimsPrincipal user, CancellationToken ct)
    {
        var ticket = await _q.LoadAsync(slug, number, false, ct);
        TicketAccess.EnsureCanRead(user, ticket, ticket.Project);
        if (eventId is not null) await EnsureCommentAsync(ticket, eventId.Value, user, ct);
        return await BuildAsync(ticket.Id, eventId, user.GetUserId(), ct);
    }

    /// <summary><paramref name="mode"/>: add (idempotent), remove, toggle (bấm lại = gỡ).</summary>
    public async Task<ReactionSummaryResponse> MutateAsync(string slug, int number, Guid? eventId, ReactionType type, string mode, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(user.HasPermission(Permissions.TicketComment), "Reaction cần quyền bình luận.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        var ticketId = await _q.InTransactionAsync(async () =>
        {
            var probe = await _q.LoadAsync(slug, number, false, ct);
            await _q.LockTicketRowAsync(probe.Id, ct);
            var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);
            TicketAccess.EnsureCanRead(user, ticket, ticket.Project);
            if (ticket.IsLocked)
            {
                throw AppException.Forbidden("Hội thoại đã bị khoá; reaction bị tắt với mọi người (BR-LIFECYCLE-02).");
            }
            if (eventId is not null) await EnsureCommentAsync(ticket, eventId.Value, user, ct);

            var existing = await _db.TicketReactions.FirstOrDefaultAsync(r => r.TicketId == ticket.Id && r.EventId == eventId && r.UserId == actorId && r.ReactionType == type, ct);
            var add = mode switch { "add" => true, "remove" => false, _ => existing is null };

            if (add && existing is null)
            {
                _db.TicketReactions.Add(new TicketReaction { Id = Guid.NewGuid(), TicketId = ticket.Id, EventId = eventId, UserId = actorId, ReactionType = type, CreatedAt = now });
                await _events.AppendAsync(ticket, TicketEventTypes.Reacted, new { reaction_type = EnumNaming.Format(type), target_event_id = eventId }, actorId, at: now, ct: ct);
            }
            else if (!add && existing is not null)
            {
                _db.TicketReactions.Remove(existing);
                await _events.AppendAsync(ticket, TicketEventTypes.Unreacted, new { reaction_type = EnumNaming.Format(type), target_event_id = eventId }, actorId, at: now, ct: ct);
            }
            else
            {
                return ticket.Id; // idempotent
            }

            if (eventId is null)
            {
                await _db.SaveChangesAsync(ct);
                ticket.ReactionsSummary = JsonSerializer.Serialize(await CountsAsync(ticket.Id, null, ct));
            }
            await _db.SaveIgnoringDuplicateAsync(ct);
            return ticket.Id;
        }, ct);

        return await BuildAsync(ticketId, eventId, actorId, ct);
    }

    private async Task EnsureCommentAsync(Ticket ticket, Guid eventId, ClaimsPrincipal user, CancellationToken ct)
    {
        var commentTypes = new[] { TicketEventTypes.Commented, TicketEventTypes.InternalNote };
        var evt = await _db.TicketEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.TicketId == ticket.Id && commentTypes.Contains(e.EventType), ct)
                  ?? throw AppException.NotFound("Không tìm thấy comment.");
        if (!TimelineAccess.CanSee(user, evt)) throw AppException.NotFound("Không tìm thấy comment.");
    }

    private async Task<Dictionary<string, int>> CountsAsync(Guid ticketId, Guid? eventId, CancellationToken ct)
    {
        var rows = await _db.TicketReactions.Where(r => r.TicketId == ticketId && r.EventId == eventId)
            .GroupBy(r => r.ReactionType).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var dict = rows.ToDictionary(r => EnumNaming.Format(r.Key), r => r.Count);
        dict["total"] = rows.Sum(r => r.Count);
        return dict;
    }

    private async Task<ReactionSummaryResponse> BuildAsync(Guid ticketId, Guid? eventId, Guid viewerId, CancellationToken ct)
    {
        var counts = await CountsAsync(ticketId, eventId, ct);
        var total = counts["total"];
        counts.Remove("total");
        var mine = await _db.TicketReactions.Where(r => r.TicketId == ticketId && r.EventId == eventId && r.UserId == viewerId).Select(r => r.ReactionType).ToListAsync(ct);
        return new ReactionSummaryResponse(counts, mine, total);
    }
}

[ApiController]
[Route("api/projects/{project}/tickets/{number:int}")]
[Produces("application/json")]
public sealed class ReactionsController : ControllerBase
{
    private readonly ReactionService _reactions;
    public ReactionsController(ReactionService reactions) => _reactions = reactions;

    [HttpGet("reactions")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<ReactionSummaryResponse>> Ticket(string project, int number, CancellationToken ct)
        => Ok(await _reactions.SummaryAsync(project, number, null, User, ct));

    [HttpPost("reactions")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<ReactionSummaryResponse>> AddToTicket(string project, int number, ReactionRequest request, CancellationToken ct)
        => Ok(await _reactions.MutateAsync(project, number, null, ReactionService.Parse(request.Content), "add", User, ct));

    [HttpPost("reactions/toggle")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<ReactionSummaryResponse>> ToggleOnTicket(string project, int number, ReactionRequest request, CancellationToken ct)
        => Ok(await _reactions.MutateAsync(project, number, null, ReactionService.Parse(request.Content), "toggle", User, ct));

    [HttpDelete("reactions")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<ReactionSummaryResponse>> RemoveFromTicket(string project, int number, ReactionRequest request, CancellationToken ct)
        => Ok(await _reactions.MutateAsync(project, number, null, ReactionService.Parse(request.Content), "remove", User, ct));

    [HttpGet("comments/{commentId:guid}/reactions")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<ReactionSummaryResponse>> Comment(string project, int number, Guid commentId, CancellationToken ct)
        => Ok(await _reactions.SummaryAsync(project, number, commentId, User, ct));

    [HttpPost("comments/{commentId:guid}/reactions")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<ReactionSummaryResponse>> AddToComment(string project, int number, Guid commentId, ReactionRequest request, CancellationToken ct)
        => Ok(await _reactions.MutateAsync(project, number, commentId, ReactionService.Parse(request.Content), "add", User, ct));

    [HttpPost("comments/{commentId:guid}/reactions/toggle")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<ReactionSummaryResponse>> ToggleOnComment(string project, int number, Guid commentId, ReactionRequest request, CancellationToken ct)
        => Ok(await _reactions.MutateAsync(project, number, commentId, ReactionService.Parse(request.Content), "toggle", User, ct));

    [HttpDelete("comments/{commentId:guid}/reactions")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<ReactionSummaryResponse>> RemoveFromComment(string project, int number, Guid commentId, ReactionRequest request, CancellationToken ct)
        => Ok(await _reactions.MutateAsync(project, number, commentId, ReactionService.Parse(request.Content), "remove", User, ct));
}
