using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IncidentTracker.Api.Modules.Tickets;

/// <summary>
/// API Contract mục 6.5 (Architecture v3.1) — REST theo GitHub Issues, URL dùng <c>number</c> per project.
/// Controller chỉ điều phối; quy tắc nằm ở <see cref="TicketService"/>.
/// </summary>
[ApiController]
[Route("api/projects/{project}/tickets")]
[Produces("application/json")]
public sealed class TicketsController : ControllerBase
{
    private readonly TicketService _tickets;
    private readonly TimelineService _timeline;
    private readonly TicketQueries _q;
    private readonly TicketExtensions _ext;

    public TicketsController(TicketService tickets, TimelineService timeline, TicketQueries q, TicketExtensions ext)
    {
        _tickets = tickets;
        _timeline = timeline;
        _q = q;
        _ext = ext;
    }

    /// <summary>Danh sách ticket (cursor). Query DSL qua <c>q</c> (mục 2.7).</summary>
    [HttpGet]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    [ProducesResponseType(typeof(CursorPage<TicketResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<CursorPage<TicketResponse>>> List(string project, [FromQuery] TicketListQuery query, CancellationToken ct)
    {
        var page = await _tickets.ListAsync(project, query, User, _ext.DslFilter, ct);
        SetLink(page.NextCursor);
        return Ok(page);
    }

    /// <summary>Tạo ticket (bắt buộc Idempotency-Key — BR-REL-01).</summary>
    [HttpPost]
    [RequirePermission(Permissions.TicketCreate)]
    [RequireIdempotencyKey]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(typeof(TicketResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<TicketResponse>> Create(string project, CreateTicketRequest request, CancellationToken ct)
    {
        var created = await _tickets.CreateAsync(project, request, User, _ext.TemplateRenderer, _ext.AttachParent, ct);
        EntityTags.SetETag(Response, created.Version);
        return CreatedAtAction(nameof(Get), new { project = created.ProjectSlug, number = created.Number }, created);
    }

    /// <summary>Snapshot ticket (mục 5.2), kèm <c>ETag</c>.</summary>
    [HttpGet("{number:int}")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    [ProducesResponseType(typeof(TicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status301MovedPermanently)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TicketResponse>> Get(string project, int number, CancellationToken ct)
    {
        var ticket = await _tickets.GetAsync(project, number, User, ct);
        EntityTags.SetETag(Response, ticket.Version);
        return Ok(ticket);
    }

    /// <summary>
    /// Sửa title/body/state/labels/assignees/milestone/type/priority (≈ <c>PATCH /repos/{o}/{r}/issues/{n}</c>).
    /// Hỗ trợ <c>If-Match</c> → 412 (BR-CONC-01). Đóng cha còn sub-issue mở → 200 + <c>warnings</c> (BR-REL-03).
    /// </summary>
    [HttpPatch("{number:int}")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(typeof(TicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TicketResponse>> Update(string project, int number, UpdateTicketRequest request, CancellationToken ct)
    {
        var updated = await _tickets.UpdateAsync(project, number, request, User, Request, _ext.CloseWarnings, ct);
        EntityTags.SetETag(Response, updated.Version);
        return Ok(updated);
    }

    /// <summary>Xoá ticket vĩnh viễn — Admin (BR-LIFECYCLE-05).</summary>
    [HttpDelete("{number:int}")]
    [RequirePermission(Permissions.TicketDelete)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string project, int number, CancellationToken ct)
    {
        await _tickets.DeleteAsync(project, number, User, ct);
        return NoContent();
    }

    /// <summary>Timeline hợp nhất, keyset theo <c>sequence</c>, lọc visibility (BR-SEC-06).</summary>
    [HttpGet("{number:int}/timeline")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    [ProducesResponseType(typeof(CursorPage<TimelineEventDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<CursorPage<TimelineEventDto>>> Timeline(string project, int number,
        [FromQuery] string? cursor, [FromQuery(Name = "per_page")] int? perPage, CancellationToken ct)
    {
        var ticket = await _q.LoadAsync(project, number, false, ct);
        TicketAccess.EnsureCanRead(User, ticket, ticket.Project);
        var page = await _timeline.ListAsync(ticket, cursor, perPage, User, ct);
        SetLink(page.NextCursor);
        return Ok(page);
    }

    // ---- lock / pin / transfer ----

    [HttpPut("{number:int}/lock")]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<TicketResponse>> Lock(string project, int number, LockRequest? request, CancellationToken ct)
        => Ok(await _tickets.LockAsync(project, number, request?.LockReason, true, User, ct));

    [HttpDelete("{number:int}/lock")]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<TicketResponse>> Unlock(string project, int number, CancellationToken ct)
        => Ok(await _tickets.LockAsync(project, number, null, false, User, ct));

    [HttpPut("{number:int}/pin")]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<TicketResponse>> Pin(string project, int number, CancellationToken ct)
        => Ok(await _tickets.PinAsync(project, number, true, User, ct));

    [HttpDelete("{number:int}/pin")]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<TicketResponse>> Unpin(string project, int number, CancellationToken ct)
        => Ok(await _tickets.PinAsync(project, number, false, User, ct));

    /// <summary>BR-REL-05: chuyển sang project khác; URL cũ trả 301.</summary>
    [HttpPost("{number:int}/transfer")]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<TicketResponse>> Transfer(string project, int number, TransferRequest request, CancellationToken ct)
    {
        var moved = await _tickets.TransferAsync(project, number, request.ToProject, User, ct);
        Response.Headers.Location = Url.Action(nameof(Get), new { project = moved.ProjectSlug, number = moved.Number });
        return Ok(moved);
    }

    private void SetLink(string? nextCursor)
    {
        if (nextCursor is null) return;
        var path = Request.Path.Value ?? string.Empty;
        var query = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Request.Query) query[k] = v;
        query["cursor"] = nextCursor;
        var qs = string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? string.Empty)}"));
        Response.Headers.Link = $"<{path}?{qs}>; rel=\"next\"";
    }
}

/// <summary>Tra cứu theo uuid nội bộ → 301 tới URL theo project/number (mục 6.5).</summary>
[ApiController]
[Route("api/tickets")]
public sealed class TicketRedirectController : ControllerBase
{
    private readonly TicketQueries _q;
    public TicketRedirectController(TicketQueries q) => _q = q;

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.TicketRead)]
    public async Task<IActionResult> ById(Guid id, CancellationToken ct)
    {
        var t = await _q.LoadByIdAsync(id, false, ct);
        return RedirectPermanent($"/api/projects/{t.Project.Slug}/tickets/{t.Number}");
    }
}

/// <summary>
/// Điểm cắm cho các bước sau (modify_04 template/DSL, modify_05 sub-issue) để controller không
/// phải đổi chữ ký mỗi lần thêm tính năng. Đăng ký singleton; các bước sau gán delegate.
/// </summary>
public sealed class TicketExtensions
{
    public Func<Project, CreateTicketRequest, System.Security.Claims.ClaimsPrincipal, CancellationToken, Task<(string Title, string Body, TemplateDefaults? Defaults)>>? TemplateRenderer { get; set; }
    public Func<Ticket, int, System.Security.Claims.ClaimsPrincipal, CancellationToken, Task>? AttachParent { get; set; }
    public Func<Ticket, CancellationToken, Task<IReadOnlyList<string>>>? CloseWarnings { get; set; }
    public Func<IQueryable<Ticket>, string, Project, System.Security.Claims.ClaimsPrincipal, Task<IQueryable<Ticket>>>? DslFilter { get; set; }
}
