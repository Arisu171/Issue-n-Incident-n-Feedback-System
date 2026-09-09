using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Modules.Revisions;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Modules.Tickets.Organization;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace IncidentTracker.Api.Modules.Incidents;

/// <summary>
/// CMP-06 Incident Controller — chỉ điều phối. Toàn bộ quy tắc vòng đời nằm trong
/// <see cref="IncidentStateMachine"/> và <see cref="IncidentService"/> (ADR-002, DRV-05).
/// </summary>
/// <remarks>
/// <para><b>Đường dẫn mang <c>{project}</c>.</b> Không phải để đẹp: phép kiểm quyền đọc slug ngay
/// từ route (<c>PermissionAuthorizationHandler</c>), nên đặt sự cố dưới đường dẫn này là đủ để nó
/// được cách ly theo project — không phải sửa gì thêm ở tầng authorization.</para>
///
/// <para>Slug đặc biệt <c>uncategorized</c> trỏ tới sự cố **chưa gắn project nào**
/// (<c>ProjectId IS NULL</c>). Nó là project ảo, không có hàng trong bảng <c>projects</c> —
/// xem <see cref="VirtualProject"/>.</para>
///
/// <para><b>Chi tiết sự cố vẫn kiểm project.</b> Biết id của một sự cố ở project khác rồi ghép
/// vào slug mình có quyền là đường vòng hiển nhiên nhất, nên mọi thao tác trên một sự cố cụ thể
/// đều đối chiếu <c>ProjectId</c> của nó với slug trên đường dẫn.</para>
/// </remarks>
[ApiController]
[Route("api/projects/{project}/incidents")]
[Produces("application/json")]
public sealed class IncidentsController : ControllerBase
{
    private readonly IncidentService _incidents;
    private readonly AppDbContext _db;

    public IncidentsController(IncidentService incidents, AppDbContext db)
    {
        _incidents = incidents;
        _db = db;
    }

    private Task<Guid?> ScopeAsync(string project, CancellationToken ct)
        => VirtualProject.ResolveAsync(_db, project, User, ct);

    /// <summary>API-Incident-List · FR-BIZ-08.</summary>
    [HttpGet]
    [RequirePermission(Permissions.IncidentRead)]
    [ProducesResponseType(typeof(PagedResult<IncidentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<IncidentResponse>>> List(
        string project, [FromQuery] IncidentListQuery query, CancellationToken ct)
        => Ok(await _incidents.ListAsync(await ScopeAsync(project, ct), query, User, ct));

    /// <summary>Người có thể nhận sự cố. Khoá theo <c>incident.assign</c> — đúng nhóm mở được ô chọn.</summary>
    [HttpGet("assignable-users")]
    [RequirePermission(Permissions.IncidentAssign)]
    [ProducesResponseType(typeof(IReadOnlyList<AssignableUser>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AssignableUser>>> AssignableUsers(
        string project, [FromQuery] string? search, CancellationToken ct)
    {
        await ScopeAsync(project, ct);
        return Ok(await _incidents.AssignableUsersAsync(search, ct));
    }

    /// <summary>API-Incident-Detail.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.IncidentRead)]
    [ProducesResponseType(typeof(IncidentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IncidentResponse>> Get(string project, Guid id, CancellationToken ct)
        => Ok(await _incidents.GetAsync(id, User, await ScopeAsync(project, ct), ct));

    /// <summary>API-Incident-History · FR-BIZ-09.</summary>
    [HttpGet("{id:guid}/history")]
    [RequirePermission(Permissions.IncidentRead)]
    [ProducesResponseType(typeof(IReadOnlyList<StatusHistoryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<StatusHistoryResponse>>> History(
        string project, Guid id, CancellationToken ct)
        => Ok(await _incidents.GetHistoryAsync(id, User, await ScopeAsync(project, ct), ct));

    /// <summary>API-Incident-Create · FR-BIZ-01.</summary>
    [HttpPost]
    [RequirePermission(Permissions.IncidentCreate)]
    [ProducesResponseType(typeof(IncidentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IncidentResponse>> Create(
        string project, CreateIncidentRequest request, CancellationToken ct)
    {
        var created = await _incidents.CreateAsync(request, User.GetUserId(), await ScopeAsync(project, ct), ct);
        return CreatedAtAction(nameof(Get), new { project, id = created.Id }, created);
    }

    /// <summary>
    /// API-Incident-Update — sửa tiêu đề, mô tả, mức độ.
    ///
    /// Khoá bằng <c>incident.read</c> chứ không phải một permission riêng: câu hỏi "được sửa
    /// bài của ai" là câu hỏi về **bản ghi**, không phải về loại hành động, nên nó thuộc về
    /// <c>ResourceAccessRules</c> chứ không thuộc về bảng phân quyền — đúng chỗ module Ticket
    /// đã đặt nó.
    /// </summary>
    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.IncidentRead)]
    [ProducesResponseType(typeof(IncidentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IncidentResponse>> Update(
        string project, Guid id, UpdateIncidentRequest request, CancellationToken ct)
    {
        var updated = await _incidents.UpdateContentAsync(
            id, request, User, await ScopeAsync(project, ct), Request, ct);
        EntityTags.SetETag(Response, updated.Version);
        return Ok(updated);
    }

    /// <summary>
    /// API-Incident-EditClaim — "tôi đang mở form sửa sự cố này".
    ///
    /// <c>PUT</c> vì nó idempotent: gọi lại chỉ gia hạn chính chỗ đang giữ, và client gọi lại
    /// theo nhịp chừng nào form còn mở.
    /// </summary>
    [HttpPut("{id:guid}/edit-claim")]
    [RequirePermission(Permissions.IncidentRead)]
    [ProducesResponseType(typeof(EditClaimResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EditClaimResponse>> ClaimEdit(
        string project, Guid id, CancellationToken ct)
        => Ok(await _incidents.ClaimEditAsync(id, User, await ScopeAsync(project, ct), ct));

    /// <summary>API-Incident-EditClaim-Release — đóng form thì trả chỗ lại.</summary>
    [HttpDelete("{id:guid}/edit-claim")]
    [RequirePermission(Permissions.IncidentRead)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReleaseEdit(string project, Guid id, CancellationToken ct)
    {
        await _incidents.ReleaseEditAsync(id, User, await ScopeAsync(project, ct), ct);
        return NoContent();
    }

    /// <summary>API-Incident-Revisions — ai đã sửa gì, từ giá trị nào sang giá trị nào.</summary>
    [HttpGet("{id:guid}/revisions")]
    [RequirePermission(Permissions.IncidentRead)]
    [ProducesResponseType(typeof(IReadOnlyList<RevisionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RevisionResponse>>> Revisions(
        string project, Guid id, CancellationToken ct)
        => Ok(await _incidents.GetRevisionsAsync(id, User, await ScopeAsync(project, ct), ct));

    /// <summary>
    /// API-Incident-Status · FR-BIZ-02/03/05.
    /// Policy tĩnh chặn ai không có <c>incident.update_status</c>;
    /// <see cref="RequireStatusPermissionAttribute"/> chặn tiếp bước Resolved khi thiếu
    /// <c>incident.resolve</c> — cả hai đều dừng trước thân action.
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    [RequirePermission(Permissions.IncidentUpdateStatus)]
    [RequireStatusPermission]
    [ProducesResponseType(typeof(IncidentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IncidentResponse>> UpdateStatus(
        string project, Guid id, UpdateStatusRequest request, CancellationToken ct)
        => Ok(await _incidents.TransitionStatusAsync(id, request, User, await ScopeAsync(project, ct), ct));

    /// <summary>API-Incident-Assign · FR-BIZ-06.</summary>
    [HttpPut("{id:guid}/assignee/{userId:guid}")]
    [RequirePermission(Permissions.IncidentAssign)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Assign(string project, Guid id, Guid userId, CancellationToken ct)
    {
        await _incidents.AssignAsync(id, userId, User.GetUserId(), await ScopeAsync(project, ct), ct);
        return NoContent();
    }

    /// <summary>
    /// Chuyển sự cố sang project khác — thao tác phân loại cho mục <c>uncategorized</c>.
    ///
    /// Khoá theo <c>project.member.manage</c>: phân loại là việc của người quản lý project, cùng
    /// nhóm được xem mục chưa phân loại. Dùng chung một điều kiện để không có chuyện thấy mà
    /// không sửa được, hay sửa được thứ mình không thấy.
    /// </summary>
    [HttpPost("{id:guid}/transfer")]
    [RequirePermission(Permissions.ProjectMemberManage)]
    [ProducesResponseType(typeof(IncidentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IncidentResponse>> Transfer(
        string project, Guid id, TransferProjectRequest request, CancellationToken ct)
    {
        var from = await ScopeAsync(project, ct);
        var to = await VirtualProject.ResolveAsync(_db, request.ToProject, User, ct);
        return Ok(await _incidents.TransferAsync(id, from, to, User.GetUserId(), ct));
    }

    /// <summary>API-Incident-Delete · FR-BIZ-04 · ADR-003.</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.IncidentDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(string project, Guid id, CancellationToken ct)
    {
        await _incidents.SoftDeleteAsync(id, User.GetUserId(), await ScopeAsync(project, ct), ct);
        return NoContent();
    }
}
