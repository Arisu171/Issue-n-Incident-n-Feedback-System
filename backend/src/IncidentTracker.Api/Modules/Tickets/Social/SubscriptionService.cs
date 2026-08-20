using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Social;

public sealed record ThreadSubscriptionResponse(Guid TicketId, bool Subscribed, bool Ignored, SubscriptionReason? Reason, DateTimeOffset? UpdatedAt);
public sealed record ProjectWatchResponse(string Project, WatchLevel Level, DateTimeOffset? UpdatedAt);

public sealed class ThreadSubscriptionRequest
{
    public bool Subscribed { get; set; } = true;
    public bool Ignored { get; set; }
}

public sealed class ProjectWatchRequest
{
    public WatchLevel Level { get; set; } = WatchLevel.All;
}

/// <summary>UC-13 / BR-SOCIAL-02 / mục 2.8 (Architecture v3.1).</summary>
public sealed class SubscriptionService
{
    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly TicketEventStore _events;
    private readonly TimeProvider _clock;

    public SubscriptionService(AppDbContext db, TicketQueries q, TicketEventStore events, TimeProvider clock)
    {
        _db = db;
        _q = q;
        _events = events;
        _clock = clock;
    }

    public async Task<ThreadSubscriptionResponse> GetAsync(Guid ticketId, ClaimsPrincipal user, CancellationToken ct)
    {
        var ticket = await _q.LoadByIdAsync(ticketId, false, ct);
        TicketAccess.EnsureCanRead(user, ticket, ticket.Project);
        var sub = await _db.TicketSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TicketId == ticketId && s.UserId == user.GetUserId(), ct);
        return ToResponse(ticketId, sub);
    }

    /// <summary>Thủ công: SUBSCRIBED / UNSUBSCRIBED / IGNORED — luôn thắng auto-subscribe (BR-SOCIAL-02).</summary>
    public async Task<ThreadSubscriptionResponse> SetAsync(Guid ticketId, ThreadSubscriptionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var userId = user.GetUserId();
        var now = _clock.GetUtcNow();
        await _q.InTransactionAsync(async () =>
        {
            await _q.LockTicketRowAsync(ticketId, ct);
            var ticket = await _q.LoadByIdAsync(ticketId, true, ct);
            TicketAccess.EnsureCanRead(user, ticket, ticket.Project);

            var state = request.Ignored ? SubscriptionState.Ignored : request.Subscribed ? SubscriptionState.Subscribed : SubscriptionState.Unsubscribed;
            var sub = await _db.TicketSubscriptions.FirstOrDefaultAsync(s => s.TicketId == ticketId && s.UserId == userId, ct);
            if (sub is null)
            {
                sub = new TicketSubscription { TicketId = ticketId, UserId = userId };
                _db.TicketSubscriptions.Add(sub);
            }
            var changed = sub.State != state;
            sub.State = state;
            sub.Reason = SubscriptionReason.Manual;
            sub.UpdatedAt = now;

            if (changed)
            {
                await _events.AppendAsync(ticket, state == SubscriptionState.Subscribed ? TicketEventTypes.Subscribed : TicketEventTypes.Unsubscribed,
                    new { user_id = userId, reason = "MANUAL", state = EnumNaming.Format(state) }, userId, EventVisibility.ActorOnly, now, ct);
            }
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
        return await GetAsync(ticketId, user, ct);
    }

    public async Task DeleteAsync(Guid ticketId, ClaimsPrincipal user, CancellationToken ct)
        => await SetAsync(ticketId, new ThreadSubscriptionRequest { Subscribed = false }, user, ct);

    /// <summary>
    /// Auto-subscribe theo mục 2.8. Trạng thái thủ công UNSUBSCRIBED/IGNORED không bị ghi đè, trừ khi
    /// được @mention trực tiếp (chỉ ghi đè UNSUBSCRIBED; IGNORED vẫn giữ — giống GitHub).
    /// Không SaveChanges — caller gom.
    /// </summary>
    public async Task AutoSubscribeAsync(Guid ticketId, Guid userId, SubscriptionReason reason, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var sub = await _db.TicketSubscriptions.FirstOrDefaultAsync(s => s.TicketId == ticketId && s.UserId == userId, ct);
        if (sub is null)
        {
            _db.TicketSubscriptions.Add(new TicketSubscription { TicketId = ticketId, UserId = userId, State = SubscriptionState.Subscribed, Reason = reason, UpdatedAt = now });
            return;
        }
        if (sub.State == SubscriptionState.Subscribed) return;
        if (sub.State == SubscriptionState.Unsubscribed && reason == SubscriptionReason.Mentioned)
        {
            sub.State = SubscriptionState.Subscribed;
            sub.Reason = reason;
            sub.UpdatedAt = now;
        }
    }

    // ---- project watch ----

    public async Task<ProjectWatchResponse> GetWatchAsync(string slug, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var w = await _db.ProjectWatches.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == project.Id && x.UserId == user.GetUserId(), ct);
        return new ProjectWatchResponse(project.Slug, w?.Level ?? WatchLevel.Participating, w?.UpdatedAt);
    }

    public async Task<ProjectWatchResponse> SetWatchAsync(string slug, WatchLevel level, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var userId = user.GetUserId();
        var w = await _db.ProjectWatches.FirstOrDefaultAsync(x => x.ProjectId == project.Id && x.UserId == userId, ct);
        if (w is null)
        {
            w = new ProjectWatch { ProjectId = project.Id, UserId = userId };
            _db.ProjectWatches.Add(w);
        }
        w.Level = level;
        w.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveIgnoringDuplicateAsync(ct);
        return new ProjectWatchResponse(project.Slug, level, w.UpdatedAt);
    }

    public async Task DeleteWatchAsync(string slug, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        await _db.ProjectWatches.Where(x => x.ProjectId == project.Id && x.UserId == user.GetUserId()).ExecuteDeleteAsync(ct);
    }

    private static ThreadSubscriptionResponse ToResponse(Guid ticketId, TicketSubscription? sub)
        => new(ticketId, sub?.State == SubscriptionState.Subscribed, sub?.State == SubscriptionState.Ignored, sub?.Reason, sub?.UpdatedAt);
}

/// <summary>≈ <c>/notifications/threads/{id}/subscription</c> (thread id = ticket id) và <c>/repos/{o}/{r}/subscription</c>.</summary>
[ApiController]
[Produces("application/json")]
public sealed class SubscriptionsController : ControllerBase
{
    private readonly SubscriptionService _subs;
    public SubscriptionsController(SubscriptionService subs) => _subs = subs;

    [HttpGet("api/notifications/threads/{ticketId:guid}/subscription")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<ThreadSubscriptionResponse>> Get(Guid ticketId, CancellationToken ct)
        => Ok(await _subs.GetAsync(ticketId, User, ct));

    [HttpPut("api/notifications/threads/{ticketId:guid}/subscription")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<ThreadSubscriptionResponse>> Set(Guid ticketId, ThreadSubscriptionRequest request, CancellationToken ct)
        => Ok(await _subs.SetAsync(ticketId, request, User, ct));

    [HttpDelete("api/notifications/threads/{ticketId:guid}/subscription")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> Delete(Guid ticketId, CancellationToken ct)
    {
        await _subs.DeleteAsync(ticketId, User, ct);
        return NoContent();
    }

    [HttpGet("api/projects/{project}/subscription")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<ProjectWatchResponse>> GetWatch(string project, CancellationToken ct)
        => Ok(await _subs.GetWatchAsync(project, User, ct));

    [HttpPut("api/projects/{project}/subscription")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<ProjectWatchResponse>> SetWatch(string project, ProjectWatchRequest request, CancellationToken ct)
        => Ok(await _subs.SetWatchAsync(project, request.Level, User, ct));

    [HttpDelete("api/projects/{project}/subscription")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> DeleteWatch(string project, CancellationToken ct)
    {
        await _subs.DeleteWatchAsync(project, User, ct);
        return NoContent();
    }
}
