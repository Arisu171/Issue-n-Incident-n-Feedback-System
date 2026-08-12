using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Organization;

/// <summary>BR-ORG-06 — Issue Type cấp toàn hệ thống (≈ <c>/orgs/{org}/issue-types</c>).</summary>
public sealed class IssueTypeService
{
    private readonly AppDbContext _db;
    public IssueTypeService(AppDbContext db) => _db = db;

    public async Task<IReadOnlyList<IssueTypeResponse>> ListAsync(bool includeDisabled, CancellationToken ct)
    {
        var q = _db.IssueTypes.AsNoTracking();
        if (!includeDisabled) q = q.Where(t => t.IsEnabled);
        return (await q.OrderBy(t => t.Position).ThenBy(t => t.Name).ToListAsync(ct)).Select(TicketQueries.ToResponse).ToList();
    }

    public async Task<IssueTypeResponse> CreateAsync(IssueTypeRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (await _db.IssueTypes.AnyAsync(t => t.Name.ToLower() == name.ToLower(), ct))
        {
            throw new AppException(StatusCodes.Status422UnprocessableEntity, "Issue Type đã tồn tại", $"Đã có Issue Type '{name}'.");
        }
        var position = (await _db.IssueTypes.MaxAsync(t => (int?)t.Position, ct) ?? -1) + 1;
        var type = new IssueType { Id = Guid.NewGuid(), Name = name, Color = request.Color ?? "gray", Description = request.Description?.Trim(), IsEnabled = request.IsEnabled ?? true, Position = position };
        _db.IssueTypes.Add(type);
        await _db.SaveTranslatingConflictAsync($"Đã có Issue Type '{name}'.", ct);
        return TicketQueries.ToResponse(type);
    }

    public async Task<IssueTypeResponse> UpdateAsync(Guid id, IssueTypeRequest request, CancellationToken ct)
    {
        var type = await _db.IssueTypes.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw AppException.NotFound("Không tìm thấy Issue Type.");
        var name = request.Name.Trim();
        if (!string.Equals(name, type.Name, StringComparison.OrdinalIgnoreCase) && await _db.IssueTypes.AnyAsync(t => t.Name.ToLower() == name.ToLower(), ct))
        {
            throw new AppException(StatusCodes.Status422UnprocessableEntity, "Issue Type đã tồn tại", $"Đã có Issue Type '{name}'.");
        }
        type.Name = name;
        if (request.Color is not null) type.Color = request.Color;
        if (request.Description is not null) type.Description = request.Description.Trim();
        if (request.IsEnabled is { } enabled) type.IsEnabled = enabled; // tắt: ticket cũ vẫn giữ type (BR-ORG-06)
        await _db.SaveTranslatingConflictAsync("Issue Type đã tồn tại.", ct);
        return TicketQueries.ToResponse(type);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var type = await _db.IssueTypes.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw AppException.NotFound("Không tìm thấy Issue Type.");
        var used = await _db.Tickets.IgnoreQueryFilters().CountAsync(t => t.TypeId == id, ct);
        if (used > 0)
        {
            throw AppException.Conflict($"Issue Type '{type.Name}' đang được {used} ticket dùng; hãy tắt (is_enabled=false) thay vì xoá.",
                new Dictionary<string, object?> { ["tickets"] = used });
        }
        _db.IssueTypes.Remove(type);
        await _db.SaveChangesAsync(ct);
    }
}

[ApiController]
[Route("api/issue-types")]
[Produces("application/json")]
public sealed class IssueTypesController : ControllerBase
{
    private readonly IssueTypeService _types;
    public IssueTypesController(IssueTypeService types) => _types = types;

    [HttpGet]
    [RequireGlobalPermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<IssueTypeResponse>>> List([FromQuery] bool includeDisabled, CancellationToken ct)
        => Ok(await _types.ListAsync(includeDisabled, ct));

    [HttpPost]
    [RequireGlobalPermission(Permissions.IssueTypeManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<IssueTypeResponse>> Create(IssueTypeRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _types.CreateAsync(request, ct));

    [HttpPatch("{id:guid}")]
    [RequireGlobalPermission(Permissions.IssueTypeManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<IssueTypeResponse>> Update(Guid id, IssueTypeRequest request, CancellationToken ct)
        => Ok(await _types.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    [RequireGlobalPermission(Permissions.IssueTypeManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _types.DeleteAsync(id, ct);
        return NoContent();
    }
}

/// <summary>≈ <c>/issues/{n}/assignees</c> — thêm/gỡ assignee (BR-ORG-03: ≤10; mức Read chỉ tự assign).</summary>
[ApiController]
[Route("api/projects/{project}/tickets/{number:int}/assignees")]
[Produces("application/json")]
public sealed class AssigneesController : ControllerBase
{
    private readonly TicketService _tickets;
    private readonly TicketQueries _q;
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public AssigneesController(TicketService tickets, TicketQueries q, AppDbContext db, TimeProvider clock)
    {
        _tickets = tickets;
        _q = q;
        _db = db;
        _clock = clock;
    }

    [HttpPost]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<TicketResponse>> Add(string project, int number, TicketAssigneesRequest request, CancellationToken ct)
        => Ok(await MutateAsync(project, number, request.Assignees, add: true, User, ct));

    [HttpDelete]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<TicketResponse>> Remove(string project, int number, TicketAssigneesRequest request, CancellationToken ct)
        => Ok(await MutateAsync(project, number, request.Assignees, add: false, User, ct));

    private async Task<TicketResponse> MutateAsync(string slug, int number, List<string> logins, bool add, ClaimsPrincipal user, CancellationToken ct)
    {
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();
        var id = await _q.InTransactionAsync(async () =>
        {
            var probe = await _q.LoadAsync(slug, number, false, ct);
            await _q.LockTicketRowAsync(probe.Id, ct);
            var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);
            TicketAccess.EnsureCanRead(user, ticket, ticket.Project);

            var users = await _tickets.ResolveUsersAsync(logins, ct);
            var touchesOthers = users.Any(u => u.Id != actorId);
            TicketAccess.Ensure(!touchesOthers || TicketAccess.CanTriage(user), "Assign người khác cần quyền Triage; mức Read chỉ tự assign chính mình.");

            foreach (var u in users)
            {
                var existing = ticket.Assignees.FirstOrDefault(a => a.UserId == u.Id);
                if (add && existing is null) await _tickets.AssignAsync(ticket, u, actorId, now, ct);
                if (!add && existing is not null) await _tickets.UnassignAsync(ticket, existing, actorId, now, ct);
            }
            await _db.SaveChangesAsync(ct);
            return ticket.Id;
        }, ct);
        return await _q.ToResponseAsync(await _q.LoadByIdAsync(id, false, ct), user, null, ct);
    }
}
