using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace IncidentTracker.Api.Modules.Rbac;

/// <summary>CMP-01 · UC-01, UC-04 — quản lý user và gán role.</summary>
[ApiController]
[Route("api/users")]
[Produces("application/json")]
public sealed class UsersController : ControllerBase
{
    private readonly RbacService _rbac;

    public UsersController(RbacService rbac) => _rbac = rbac;

    /// <summary>API-Protected · FR-005 — danh sách user kèm role, không bao giờ trả password hash.</summary>
    [HttpGet]
    [RequireGlobalPermission(Permissions.UserRead)]
    [ProducesResponseType(typeof(PagedResult<UserResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<UserResponse>>> List(
        [FromQuery] UserListQuery query, CancellationToken ct)
        => Ok(await _rbac.ListUsersAsync(query, ct));

    [HttpGet("{id:guid}")]
    [RequireGlobalPermission(Permissions.UserRead)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> Get(Guid id, CancellationToken ct)
        => Ok(await _rbac.GetUserAsync(id, ct));

    /// <summary>API-User-Create · FR-001.</summary>
    [HttpPost]
    [RequireGlobalPermission(Permissions.UserCreate)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserResponse>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var created = await _rbac.CreateUserAsync(request, User.GetUserId(), ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>
    /// Bổ sung so với mục 6.6 để permission <c>user.update</c> của bảng 9.2 có endpoint thực thi,
    /// đồng thời phục vụ TC-BIZ-07 (cần một assignee inactive để kiểm chứng).
    /// </summary>
    [HttpPatch("{id:guid}")]
    [RequireGlobalPermission(Permissions.UserUpdate)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserResponse>> Update(Guid id, UpdateUserRequest request, CancellationToken ct)
        => Ok(await _rbac.UpdateUserAsync(id, request, User.GetUserId(), ct));

    /// <summary>API-Role-Assign · FR-003 — idempotent.</summary>
    /// <summary>UC-01 — xóa tài khoản chưa phát sinh dữ liệu nghiệp vụ.</summary>
    [HttpDelete("{id:guid}")]
    [RequireGlobalPermission(Permissions.UserDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _rbac.DeleteUserAsync(id, User.GetUserId(), ct);
        return NoContent();
    }

    [HttpPut("{userId:guid}/roles/{roleId:guid}")]
    [RequireGlobalPermission(Permissions.UserRoleAssign)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignRole(Guid userId, Guid roleId, CancellationToken ct)
    {
        await _rbac.AssignRoleAsync(userId, roleId, User.GetUserId(), ct);
        return NoContent();
    }

    [HttpDelete("{userId:guid}/roles/{roleId:guid}")]
    [RequireGlobalPermission(Permissions.UserRoleAssign)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemoveRole(Guid userId, Guid roleId, CancellationToken ct)
    {
        await _rbac.RemoveRoleAsync(userId, roleId, User.GetUserId(), ct);
        return NoContent();
    }
}

/// <summary>CMP-01 · UC-02, UC-05.</summary>
[ApiController]
[Route("api/roles")]
[Produces("application/json")]
public sealed class RolesController : ControllerBase
{
    private readonly RbacService _rbac;

    public RolesController(RbacService rbac) => _rbac = rbac;

    [HttpGet]
    [RequireGlobalPermission(Permissions.RoleRead)]
    [ProducesResponseType(typeof(IReadOnlyList<RoleResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RoleResponse>>> List(CancellationToken ct)
        => Ok(await _rbac.ListRolesAsync(ct));

    [HttpGet("{id:guid}")]
    [RequireGlobalPermission(Permissions.RoleRead)]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleResponse>> Get(Guid id, CancellationToken ct)
        => Ok(await _rbac.GetRoleAsync(id, ct));

    [HttpPost]
    [RequireGlobalPermission(Permissions.RoleWrite)]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleResponse>> Create(CreateRoleRequest request, CancellationToken ct)
    {
        var created = await _rbac.CreateRoleAsync(request, User.GetUserId(), ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPatch("{id:guid}")]
    [RequireGlobalPermission(Permissions.RoleWrite)]
    [ProducesResponseType(typeof(RoleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleResponse>> Update(Guid id, UpdateRoleRequest request, CancellationToken ct)
        => Ok(await _rbac.UpdateRoleAsync(id, request, User.GetUserId(), ct));

    [HttpDelete("{id:guid}")]
    [RequireGlobalPermission(Permissions.RoleWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _rbac.DeleteRoleAsync(id, User.GetUserId(), ct);
        return NoContent();
    }

    /// <summary>API-Perm-Assign · FR-004 — idempotent.</summary>
    [HttpPut("{roleId:guid}/permissions/{permissionId:guid}")]
    [RequireGlobalPermission(Permissions.RolePermissionAssign)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignPermission(Guid roleId, Guid permissionId, CancellationToken ct)
    {
        await _rbac.AssignPermissionAsync(roleId, permissionId, User.GetUserId(), ct);
        return NoContent();
    }

    [HttpDelete("{roleId:guid}/permissions/{permissionId:guid}")]
    [RequireGlobalPermission(Permissions.RolePermissionAssign)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RemovePermission(Guid roleId, Guid permissionId, CancellationToken ct)
    {
        await _rbac.RemovePermissionAsync(roleId, permissionId, User.GetUserId(), ct);
        return NoContent();
    }
}

/// <summary>CMP-01 · UC-03 — danh mục năng lực hệ thống.</summary>
[ApiController]
[Route("api/permissions")]
[Produces("application/json")]
public sealed class PermissionsController : ControllerBase
{
    private readonly RbacService _rbac;

    public PermissionsController(RbacService rbac) => _rbac = rbac;

    [HttpGet]
    [RequireGlobalPermission(Permissions.PermissionRead)]
    [ProducesResponseType(typeof(IReadOnlyList<PermissionResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PermissionResponse>>> List(CancellationToken ct)
        => Ok(await _rbac.ListPermissionsAsync(ct));

    [HttpPost]
    [RequireGlobalPermission(Permissions.PermissionWrite)]
    [ProducesResponseType(typeof(PermissionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PermissionResponse>> Create(CreatePermissionRequest request, CancellationToken ct)
    {
        var created = await _rbac.CreatePermissionAsync(request, User.GetUserId(), ct);
        return Created($"/api/permissions/{created.Id}", created);
    }

    [HttpGet("{id:guid}")]
    [RequireGlobalPermission(Permissions.PermissionRead)]
    [ProducesResponseType(typeof(PermissionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PermissionResponse>> Get(Guid id, CancellationToken ct)
        => Ok(await _rbac.GetPermissionAsync(id, ct));

    [HttpPatch("{id:guid}")]
    [RequireGlobalPermission(Permissions.PermissionWrite)]
    [ProducesResponseType(typeof(PermissionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PermissionResponse>> Update(
        Guid id, UpdatePermissionRequest request, CancellationToken ct)
        => Ok(await _rbac.UpdatePermissionAsync(id, request, User.GetUserId(), ct));

    [HttpDelete("{id:guid}")]
    [RequireGlobalPermission(Permissions.PermissionWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _rbac.DeletePermissionAsync(id, User.GetUserId(), ct);
        return NoContent();
    }
}
