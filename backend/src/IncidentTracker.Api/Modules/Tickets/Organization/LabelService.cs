using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Organization;

/// <summary>UC-07 / BR-ORG-02, BR-ORG-04 (Architecture v3.1).</summary>
public sealed class LabelService
{
    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly TicketService _tickets;
    private readonly TimeProvider _clock;
    private readonly MassTransit.IPublishEndpoint _publish;
    private readonly IHttpContextAccessor _http;

    public LabelService(AppDbContext db, TicketQueries q, TicketService tickets, TimeProvider clock, MassTransit.IPublishEndpoint publish, IHttpContextAccessor http)
    {
        _db = db;
        _q = q;
        _tickets = tickets;
        _clock = clock;
        _publish = publish;
        _http = http;
    }

    private Task RaiseAsync(string action, Label label, CancellationToken ct)
    {
        Guid? sender = null;
        try { sender = _http.HttpContext?.User.GetUserId(); } catch (InvalidOperationException) { }
        var payload = System.Text.Json.JsonSerializer.Serialize(new { label = new { label.Id, label.Name, color = label.ColorHex, label.Description } }, TicketEventStore.PayloadJson);
        return _publish.Publish(new Webhooks.WebhookEventRaised("label", action, label.ProjectId, payload, sender), ct);
    }

    public async Task<IReadOnlyList<LabelResponse>> ListAsync(string slug, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var labels = await _db.Labels.AsNoTracking().Where(l => l.ProjectId == project.Id && !l.IsArchived).OrderBy(l => l.Name).ToListAsync(ct);
        return labels.Select(TicketQueries.ToResponse).ToList();
    }

    public async Task<LabelResponse> CreateAsync(string slug, LabelRequest request, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var name = Normalize(request.Name);
        if (await _db.Labels.AnyAsync(l => l.ProjectId == project.Id && !l.IsArchived && l.Name == name, ct))
        {
            throw new AppException(StatusCodes.Status422UnprocessableEntity, "Label đã tồn tại", $"Label '{name}' đã có trong project (không phân biệt hoa/thường).");
        }
        var label = new Label
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Name = name, ColorHex = NormalizeColor(request.Color) ?? RandomColor(),
            Description = request.Description?.Trim()
        };
        _db.Labels.Add(label);
        await RaiseAsync("created", label, ct);
        await _db.SaveTranslatingConflictAsync($"Label '{name}' đã tồn tại.", ct);
        return TicketQueries.ToResponse(label);
    }

    /// <summary>Đổi tên/màu/mô tả không sinh event trên ticket (BR-ORG-02).</summary>
    public async Task<LabelResponse> UpdateAsync(string slug, string name, UpdateLabelRequest request, CancellationToken ct)
    {
        var label = await FindAsync(slug, name, ct);
        if (request.NewName is not null)
        {
            var newName = Normalize(request.NewName);
            if (newName != label.Name && await _db.Labels.AnyAsync(l => l.ProjectId == label.ProjectId && !l.IsArchived && l.Name == newName, ct))
            {
                throw new AppException(StatusCodes.Status422UnprocessableEntity, "Label đã tồn tại", $"Label '{newName}' đã có trong project.");
            }
            label.Name = newName;
        }
        if (request.Color is not null) label.ColorHex = NormalizeColor(request.Color)!;
        if (request.Description is not null) label.Description = request.Description.Trim();
        await RaiseAsync("edited", label, ct);
        await _db.SaveTranslatingConflictAsync("Label đã tồn tại.", ct);
        return TicketQueries.ToResponse(label);
    }

    /// <summary>Xoá = archive + gỡ khỏi mọi ticket, KHÔNG sinh UNLABELED (giống GitHub; BR-ORG-02).</summary>
    public async Task DeleteAsync(string slug, string name, CancellationToken ct)
    {
        var label = await FindAsync(slug, name, ct);
        label.IsArchived = true;
        await _db.TicketLabels.Where(tl => tl.LabelId == label.Id).ExecuteDeleteAsync(ct);
        await RaiseAsync("deleted", label, ct);
        await _db.SaveChangesAsync(ct);
    }

    // ---- gắn/gỡ trên ticket (Triage) ----

    public async Task<IReadOnlyList<LabelResponse>> AddToTicketAsync(string slug, int number, IEnumerable<string> names, ClaimsPrincipal user, CancellationToken ct)
        => await MutateAsync(slug, number, user, async (ticket, now) =>
        {
            var labels = await _tickets.ResolveLabelsAsync(ticket.ProjectId, names, ct);
            foreach (var l in labels.Where(l => ticket.Labels.All(tl => tl.LabelId != l.Id)))
            {
                await _tickets.AddLabelAsync(ticket, l, user.GetUserId(), now, ct);
            }
        }, ct);

    public async Task<IReadOnlyList<LabelResponse>> ReplaceOnTicketAsync(string slug, int number, IEnumerable<string> names, ClaimsPrincipal user, CancellationToken ct)
        => await MutateAsync(slug, number, user, async (ticket, now) =>
        {
            var labels = await _tickets.ResolveLabelsAsync(ticket.ProjectId, names, ct);
            var wanted = labels.Select(l => l.Id).ToHashSet();
            foreach (var tl in ticket.Labels.Where(tl => !wanted.Contains(tl.LabelId)).ToList())
                await _tickets.RemoveLabelAsync(ticket, tl, user.GetUserId(), now, ct);
            foreach (var l in labels.Where(l => ticket.Labels.All(tl => tl.LabelId != l.Id)))
                await _tickets.AddLabelAsync(ticket, l, user.GetUserId(), now, ct);
        }, ct);

    public async Task<IReadOnlyList<LabelResponse>> RemoveFromTicketAsync(string slug, int number, string? name, ClaimsPrincipal user, CancellationToken ct)
        => await MutateAsync(slug, number, user, async (ticket, now) =>
        {
            var targets = name is null
                ? ticket.Labels.ToList()
                : ticket.Labels.Where(tl => tl.Label.Name == Normalize(name)).ToList();
            if (name is not null && targets.Count == 0)
            {
                throw AppException.NotFound($"Ticket không có label '{name}'.");
            }
            foreach (var tl in targets) await _tickets.RemoveLabelAsync(ticket, tl, user.GetUserId(), now, ct);
        }, ct);

    private async Task<IReadOnlyList<LabelResponse>> MutateAsync(string slug, int number, ClaimsPrincipal user,
        Func<Ticket, DateTimeOffset, Task> body, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanTriage(user), "Gắn/gỡ label cần quyền Triage.");
        var now = _clock.GetUtcNow();
        var id = await _q.InTransactionAsync(async () =>
        {
            var probe = await _q.LoadAsync(slug, number, false, ct);
            await _q.LockTicketRowAsync(probe.Id, ct);
            var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);
            await body(ticket, now);
            await _db.SaveChangesAsync(ct);
            return ticket.Id;
        }, ct);
        var reloaded = await _q.LoadByIdAsync(id, false, ct);
        return reloaded.Labels.Select(l => TicketQueries.ToResponse(l.Label)).OrderBy(l => l.Name).ToList();
    }

    private async Task<Label> FindAsync(string slug, string name, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var n = Normalize(name);
        return await _db.Labels.FirstOrDefaultAsync(l => l.ProjectId == project.Id && !l.IsArchived && l.Name == n, ct)
               ?? throw AppException.NotFound($"Không tìm thấy label '{name}'.");
    }

    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    public static string? NormalizeColor(string? color) => string.IsNullOrWhiteSpace(color) ? null : color.Trim().TrimStart('#').ToLowerInvariant();

    private static string RandomColor() => Random.Shared.Next(0, 0xFFFFFF).ToString("x6");
}

[ApiController]
[Route("api/projects/{project}")]
[Produces("application/json")]
public sealed class LabelsController : ControllerBase
{
    private readonly LabelService _labels;
    public LabelsController(LabelService labels) => _labels = labels;

    [HttpGet("labels")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<LabelResponse>>> List(string project, CancellationToken ct)
        => Ok(await _labels.ListAsync(project, ct));

    [HttpPost("labels")]
    [RequirePermission(Permissions.LabelWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<LabelResponse>> Create(string project, LabelRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _labels.CreateAsync(project, request, ct));

    [HttpPatch("labels/{name}")]
    [RequirePermission(Permissions.LabelWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<LabelResponse>> Update(string project, string name, UpdateLabelRequest request, CancellationToken ct)
        => Ok(await _labels.UpdateAsync(project, name, request, ct));

    [HttpDelete("labels/{name}")]
    [RequirePermission(Permissions.LabelWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> Delete(string project, string name, CancellationToken ct)
    {
        await _labels.DeleteAsync(project, name, ct);
        return NoContent();
    }

    // ---- trên ticket (≈ /issues/{n}/labels) ----

    [HttpPost("tickets/{number:int}/labels")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<IReadOnlyList<LabelResponse>>> AddToTicket(string project, int number, TicketLabelsRequest request, CancellationToken ct)
        => Ok(await _labels.AddToTicketAsync(project, number, request.Labels, User, ct));

    [HttpPut("tickets/{number:int}/labels")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<IReadOnlyList<LabelResponse>>> ReplaceOnTicket(string project, int number, TicketLabelsRequest request, CancellationToken ct)
        => Ok(await _labels.ReplaceOnTicketAsync(project, number, request.Labels, User, ct));

    [HttpDelete("tickets/{number:int}/labels")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<IReadOnlyList<LabelResponse>>> RemoveAll(string project, int number, CancellationToken ct)
        => Ok(await _labels.RemoveFromTicketAsync(project, number, null, User, ct));

    [HttpDelete("tickets/{number:int}/labels/{name}")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<IReadOnlyList<LabelResponse>>> RemoveOne(string project, int number, string name, CancellationToken ct)
        => Ok(await _labels.RemoveFromTicketAsync(project, number, name, User, ct));
}
