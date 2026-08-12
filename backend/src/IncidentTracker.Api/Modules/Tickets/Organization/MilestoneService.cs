using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Organization;

/// <summary>UC-08 / BR-ORG-05 (Architecture v3.1).</summary>
public sealed class MilestoneService
{
    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly TimeProvider _clock;
    private readonly MassTransit.IPublishEndpoint _publish;
    private readonly IHttpContextAccessor _http;

    public MilestoneService(AppDbContext db, TicketQueries q, TimeProvider clock, MassTransit.IPublishEndpoint publish, IHttpContextAccessor http)
    {
        _db = db;
        _q = q;
        _clock = clock;
        _publish = publish;
        _http = http;
    }

    private Task RaiseAsync(string action, Milestone ms, CancellationToken ct)
    {
        Guid? sender = null;
        try { sender = _http.HttpContext?.User.GetUserId(); } catch (InvalidOperationException) { }
        var payload = System.Text.Json.JsonSerializer.Serialize(new { milestone = new { ms.Id, ms.Number, ms.Title, ms.Description, due_on = ms.DueOn, state = EnumNaming.Format(ms.State) } }, Infrastructure.TicketEventStore.PayloadJson);
        return _publish.Publish(new Webhooks.WebhookEventRaised("milestone", action, ms.ProjectId, payload, sender), ct);
    }

    public async Task<IReadOnlyList<MilestoneResponse>> ListAsync(string slug, string? state, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var q = _db.Milestones.AsNoTracking().Where(m => m.ProjectId == project.Id);
        var s = (state ?? "open").ToLowerInvariant();
        if (s == "open") q = q.Where(m => m.State == TicketState.Open);
        else if (s == "closed") q = q.Where(m => m.State == TicketState.Closed);
        var rows = await q.OrderBy(m => m.DueOn == null).ThenBy(m => m.DueOn).ThenBy(m => m.Number).ToListAsync(ct);
        return rows.Select(TicketQueries.ToResponse).ToList();
    }

    public async Task<MilestoneResponse> GetAsync(string slug, int number, CancellationToken ct)
        => TicketQueries.ToResponse(await FindAsync(slug, number, ct));

    public async Task<MilestoneResponse> CreateAsync(string slug, MilestoneRequest request, CancellationToken ct)
    {
        var id = await _q.InTransactionAsync(async () =>
        {
            var project = await _q.ProjectAsync(slug, ct);
            await _q.LockProjectRowAsync(project.Id, ct);
            var title = request.Title.Trim();
            if (await _db.Milestones.AnyAsync(m => m.ProjectId == project.Id && m.Title == title, ct))
            {
                throw new AppException(StatusCodes.Status422UnprocessableEntity, "Milestone đã tồn tại", $"Milestone '{title}' đã có trong project.");
            }
            var next = (await _db.Milestones.Where(m => m.ProjectId == project.Id).MaxAsync(m => (int?)m.Number, ct) ?? 0) + 1;
            var now = _clock.GetUtcNow();
            var ms = new Milestone
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, Number = next, Title = title,
                Description = request.Description?.Trim(), DueOn = request.DueOn, State = TicketState.Open,
                CreatedAt = now, UpdatedAt = now
            };
            _db.Milestones.Add(ms);
            await RaiseAsync("created", ms, ct);
            await _db.SaveChangesAsync(ct);
            return ms.Id;
        }, ct);
        return TicketQueries.ToResponse(await _db.Milestones.AsNoTracking().FirstAsync(m => m.Id == id, ct));
    }

    public async Task<MilestoneResponse> UpdateAsync(string slug, int number, UpdateMilestoneRequest request, CancellationToken ct)
    {
        var ms = await FindAsync(slug, number, ct);
        if (request.Title is not null)
        {
            var title = request.Title.Trim();
            if (title != ms.Title && await _db.Milestones.AnyAsync(m => m.ProjectId == ms.ProjectId && m.Title == title, ct))
            {
                throw new AppException(StatusCodes.Status422UnprocessableEntity, "Milestone đã tồn tại", $"Milestone '{title}' đã có trong project.");
            }
            ms.Title = title;
        }
        if (request.Description is not null) ms.Description = request.Description.Trim();
        if (request.ClearDueOn == true) ms.DueOn = null;
        else if (request.DueOn is { } due) ms.DueOn = due;
        var action = "edited";
        if (request.State is { } state && state != ms.State)
        {
            // BR-ORG-05: đóng milestone KHÔNG đóng ticket bên trong.
            ms.State = state;
            ms.ClosedAt = state == TicketState.Closed ? _clock.GetUtcNow() : null;
            action = state == TicketState.Closed ? "closed" : "opened";
        }
        ms.UpdatedAt = _clock.GetUtcNow();
        await RaiseAsync(action, ms, ct);
        await _db.SaveTranslatingConflictAsync("Milestone đã tồn tại.", ct);
        return TicketQueries.ToResponse(ms);
    }

    /// <summary>Xoá milestone gỡ khỏi ticket mà không sinh DEMILESTONED (BR-ORG-05, giống GitHub).</summary>
    public async Task DeleteAsync(string slug, int number, CancellationToken ct)
    {
        var ms = await FindAsync(slug, number, ct);
        await _db.Tickets.IgnoreQueryFilters().Where(t => t.MilestoneId == ms.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.MilestoneId, (Guid?)null), ct);
        _db.Milestones.Remove(ms);
        await RaiseAsync("deleted", ms, ct);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<Milestone> FindAsync(string slug, int number, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        return await _db.Milestones.FirstOrDefaultAsync(m => m.ProjectId == project.Id && m.Number == number, ct)
               ?? throw AppException.NotFound($"Không tìm thấy milestone #{number}.");
    }
}

[ApiController]
[Route("api/projects/{project}/milestones")]
[Produces("application/json")]
public sealed class MilestonesController : ControllerBase
{
    private readonly MilestoneService _milestones;
    public MilestonesController(MilestoneService milestones) => _milestones = milestones;

    [HttpGet]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<MilestoneResponse>>> List(string project, [FromQuery] string? state, CancellationToken ct)
        => Ok(await _milestones.ListAsync(project, state, ct));

    [HttpGet("{number:int}")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<MilestoneResponse>> Get(string project, int number, CancellationToken ct)
        => Ok(await _milestones.GetAsync(project, number, ct));

    [HttpPost]
    [RequireAllPermissions(Permissions.LabelWrite, Permissions.MilestoneWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<MilestoneResponse>> Create(string project, MilestoneRequest request, CancellationToken ct)
    {
        var created = await _milestones.CreateAsync(project, request, ct);
        return CreatedAtAction(nameof(Get), new { project, number = created.Number }, created);
    }

    [HttpPatch("{number:int}")]
    [RequireAllPermissions(Permissions.LabelWrite, Permissions.MilestoneWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<MilestoneResponse>> Update(string project, int number, UpdateMilestoneRequest request, CancellationToken ct)
        => Ok(await _milestones.UpdateAsync(project, number, request, ct));

    [HttpDelete("{number:int}")]
    [RequireAllPermissions(Permissions.LabelWrite, Permissions.MilestoneWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> Delete(string project, int number, CancellationToken ct)
    {
        await _milestones.DeleteAsync(project, number, ct);
        return NoContent();
    }
}
