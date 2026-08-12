using System.Security.Claims;
using System.Text.Json;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Organization;

/// <summary>≈ repository của GitHub (mục 5.1). Cấu hình project là quyền Admin (10.1).</summary>
public sealed class ProjectService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectService> _logger;
    private readonly ProjectMemberService _members;

    public ProjectService(AppDbContext db, TimeProvider clock, ILogger<ProjectService> logger,
        ProjectMemberService members)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
        _members = members;
    }

    /// <summary>
    /// Project mà người gọi với tới.
    ///
    /// Danh sách này nuôi thanh chọn project trên đầu màn hình và ô "Chuyển project". Nếu nó trả
    /// về cả những project người dùng không có quyền thì họ chọn xong chỉ nhận 403 — và tệ hơn,
    /// **tên project là dữ liệu**: liệt kê đủ tên mọi project là đã nói cho họ biết hệ thống có
    /// những gì, đúng thứ mà việc cách ly sinh ra để giấu.
    /// </summary>
    public async Task<IReadOnlyList<ProjectResponse>> ListAsync(
        bool includeArchived, ClaimsPrincipal user, CancellationToken ct)
    {
        var q = _db.Projects.AsNoTracking();
        if (!includeArchived) q = q.Where(p => !p.IsArchived);

        if (user.ProjectsWith(Permissions.TicketRead) is { } slugs)
        {
            q = q.Where(p => slugs.Contains(p.Slug));
        }

        var rows = await q.OrderBy(p => p.Name).ToListAsync(ct);
        var list = new List<ProjectResponse>();
        foreach (var p in rows) list.Add(await ToResponseAsync(p, ct));
        return list;
    }

    public async Task<ProjectResponse> GetAsync(string slug, CancellationToken ct)
        => await ToResponseAsync(await FindAsync(slug, ct), ct);

    public async Task<ProjectResponse> CreateAsync(CreateProjectRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var slug = request.Slug.Trim().ToLowerInvariant();
        VirtualProject.EnsureNotReserved(slug);
        if (await _db.Projects.AnyAsync(p => p.Slug == slug, ct))
        {
            throw AppException.Conflict($"Project '{slug}' đã tồn tại.");
        }

        var project = new Project
        {
            Id = Guid.NewGuid(), Slug = slug, Name = request.Name.Trim(), Description = request.Description?.Trim(),
            IsArchived = request.IsArchived,
            CreatedAt = _clock.GetUtcNow()
        };
        _db.Projects.Add(project);
        await _db.SaveTranslatingConflictAsync($"Project '{slug}' đã tồn tại.", ct);

        // BR-ORG-04: project mới có 9 label mặc định của GitHub.
        await TicketSeeder.EnsureDefaultLabelsAsync(_db, project.Id, ct);

        // Người tạo trở thành thành viên với đúng vai trò toàn cục của mình. Không có bước này
        // thì tạo xong một project là không vào được nó: kiểm quyền theo project không tìm thấy
        // hàng nào, và mọi endpoint dưới `api/projects/{slug}/…` trả 403.
        //
        // Project mới **không** tự cấp cho vai trò customer: mở sẵn cho mọi khách hàng là đi
        // ngược lại chính việc cách ly. Admin cấp bằng `PUT /role-access/customer` khi cần.
        await _members.AddCreatorAsync(project.Id, user.GetUserId(), ct);

        _logger.LogInformation("Audit project.create actor={ActorId} project={Slug}", user.GetUserId(), slug);
        return await ToResponseAsync(project, ct);
    }

    public async Task<ProjectResponse> UpdateAsync(string slug, UpdateProjectRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await FindAsync(slug, ct);
        if (request.Name is not null) project.Name = request.Name.Trim();
        if (request.Description is not null) project.Description = request.Description.Trim();
        if (request.BlankIssuesEnabled is { } b) project.BlankIssuesEnabled = b;
        if (request.StrictClosePolicy is { } s) project.StrictClosePolicy = s;
        if (request.AutoReopenOnCustomerComment is { } a) project.AutoReopenOnCustomerComment = a;
        if (request.CustomersSeeOnlyOwn is { } c) project.CustomersSeeOnlyOwn = c;
        if (request.IsArchived is { } ar) project.IsArchived = ar;
        if (request.ContactLinks is { } links)
        {
            if (links.ValueKind != JsonValueKind.Array) throw AppException.BadRequest("contact_links phải là mảng [{name, url, about}].");
            foreach (var l in links.EnumerateArray())
            {
                if (!l.TryGetProperty("name", out _) || !l.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String
                    || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                {
                    throw AppException.BadRequest("Mỗi contact link cần name và url http(s) hợp lệ.");
                }
            }
            project.ContactLinks = links.GetRawText();
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Audit project.update actor={ActorId} project={Slug}", user.GetUserId(), project.Slug);
        return await ToResponseAsync(project, ct);
    }

    /// <summary>
    /// Xoá hẳn một project.
    ///
    /// <b>Chỉ xoá được project rỗng.</b> Cùng luật đã áp cho việc xoá cột board: xoá một project
    /// đang có ticket, sự cố hay phản hồi là xoá luôn toàn bộ dữ liệu đó theo dây, và không có
    /// đường hoàn tác nào. Muốn dọn một project đang dùng thì <b>lưu trữ</b> nó — dữ liệu còn
    /// nguyên, chỉ không xuất hiện ở các danh sách nữa.
    /// </summary>
    public async Task DeleteAsync(string slug, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await FindAsync(slug, ct);

        var tickets = await _db.Tickets.CountAsync(t => t.ProjectId == project.Id, ct);
        var incidents = await _db.Incidents.CountAsync(i => i.ProjectId == project.Id, ct);
        var feedbacks = await _db.Feedbacks.CountAsync(f => f.ProjectId == project.Id, ct);

        if (tickets + incidents + feedbacks > 0)
        {
            throw AppException.Conflict(
                "Project còn dữ liệu nên không xoá được. Hãy chuyển hoặc xoá hết ticket, sự cố và phản hồi trước, "
                + "hoặc lưu trữ project nếu chỉ muốn ẩn nó đi.",
                new Dictionary<string, object?>
                {
                    ["tickets"] = tickets, ["incidents"] = incidents, ["feedbacks"] = feedbacks
                });
        }

        _db.Projects.Remove(project);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Audit project.delete actor={ActorId} project={Slug}", user.GetUserId(), project.Slug);
    }

    private async Task<Project> FindAsync(string slug, CancellationToken ct)
        => await _db.Projects.FirstOrDefaultAsync(p => p.Slug == slug.ToLowerInvariant(), ct)
           ?? throw AppException.NotFound($"Không tìm thấy project '{slug}'.");

    private async Task<ProjectResponse> ToResponseAsync(Project p, CancellationToken ct)
    {
        var open = await _db.Tickets.CountAsync(t => t.ProjectId == p.Id && t.State == TicketState.Open, ct);
        var closed = await _db.Tickets.CountAsync(t => t.ProjectId == p.Id && t.State == TicketState.Closed, ct);
        return new ProjectResponse(p.Id, p.Slug, p.Name, p.Description, p.BlankIssuesEnabled, p.StrictClosePolicy,
            p.AutoReopenOnCustomerComment, p.CustomersSeeOnlyOwn, JsonDocument.Parse(p.ContactLinks).RootElement.Clone(),
            p.IsArchived, open, closed, p.CreatedAt);
    }
}

[ApiController]
[Route("api/projects")]
[Produces("application/json")]
public sealed class ProjectsController : ControllerBase
{
    private readonly ProjectService _projects;
    public ProjectsController(ProjectService projects) => _projects = projects;

    [HttpGet]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<ProjectResponse>>> List([FromQuery] bool includeArchived, CancellationToken ct)
        => Ok(await _projects.ListAsync(includeArchived, User, ct));

    [HttpGet("{project}")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<ProjectResponse>> Get(string project, CancellationToken ct)
        => Ok(await _projects.GetAsync(project, ct));

    /// <summary>
    /// Tạo project mới.
    ///
    /// <b>Quyền toàn cục</b>, không phải quyền theo project: đường dẫn này không mang
    /// <c>{project}</c> nên phép kiểm thường sẽ chấp nhận cả quyền có được nhờ **một** project
    /// bất kỳ — và manager, vốn có <c>project.manage</c> để cấu hình project mình phụ trách, sẽ
    /// dựng được project mới trong cả hệ thống. Cùng loại leo thang đã chặn ở nhóm endpoint quản
    /// trị (RBAC, SLA, issue types).
    /// </summary>
    [HttpPost]
    [RequireGlobalPermission(Permissions.ProjectManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<ProjectResponse>> Create(CreateProjectRequest request, CancellationToken ct)
    {
        var created = await _projects.CreateAsync(request, User, ct);
        return CreatedAtAction(nameof(Get), new { project = created.Slug }, created);
    }

    /// <summary>
    /// Sửa cấu hình project. Đường dẫn mang <c>{project}</c> nên đây là quyền **theo project**:
    /// manager cấu hình được project mình phụ trách, và chỉ project đó.
    /// </summary>
    [HttpPatch("{project}")]
    [RequirePermission(Permissions.ProjectManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<ProjectResponse>> Update(string project, UpdateProjectRequest request, CancellationToken ct)
        => Ok(await _projects.UpdateAsync(project, request, User, ct));

    /// <summary>
    /// Xoá project rỗng. Quyền toàn cục, cùng lý do với việc tạo — và vì xoá là việc không hoàn
    /// tác được.
    /// </summary>
    [HttpDelete("{project}")]
    [RequireGlobalPermission(Permissions.ProjectManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(string project, CancellationToken ct)
    {
        await _projects.DeleteAsync(project, User, ct);
        return NoContent();
    }
}
