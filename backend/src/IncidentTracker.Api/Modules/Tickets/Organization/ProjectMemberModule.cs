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

public sealed record ProjectMemberResponse(Guid UserId, string Login, string DisplayName, IReadOnlyList<string> Roles);

public sealed record ProjectAccessResponse(
    IReadOnlyList<ProjectMemberResponse> Members,
    /// <summary>Vai trò được cấp cho **cả nhóm** — dùng cho khách hàng, không cấp từng người.</summary>
    IReadOnlyList<string> RoleAccess);

/// <summary>
/// Ai làm ở project này.
///
/// Hai đường cấp quyền, cố ý tách rời vì chúng trả lời hai câu khác nhau:
///
/// <list type="bullet">
/// <item><b>Thành viên</b> (<c>project_members</c>) — cấp cho từng người. Dùng cho nhân viên.</item>
/// <item><b>Vai trò với tới</b> (<c>project_role_access</c>) — cấp một lần cho cả vai trò. Dùng
/// cho khách hàng: với hàng nghìn khách thì cấp từng người là việc không ai làm nổi.</item>
/// </list>
/// </summary>
public sealed class ProjectMemberService
{
    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectMemberService> _logger;

    public ProjectMemberService(AppDbContext db, TicketQueries q, TimeProvider clock, ILogger<ProjectMemberService> logger)
    {
        _db = db;
        _q = q;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ProjectAccessResponse> ListAsync(string slug, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);

        var members = await _db.ProjectMembers.AsNoTracking()
            .Where(m => m.ProjectId == project.Id)
            .Select(m => new { m.UserId, m.User.Login, m.User.DisplayName, RoleName = m.Role.Name })
            .ToListAsync(ct);

        var roleAccess = await _db.ProjectRoleAccess.AsNoTracking()
            .Where(a => a.ProjectId == project.Id)
            .Select(a => a.Role.Name)
            .OrderBy(x => x)
            .ToListAsync(ct);

        return new ProjectAccessResponse(
            members
                .GroupBy(m => (m.UserId, m.Login, m.DisplayName))
                .Select(g => new ProjectMemberResponse(g.Key.UserId, g.Key.Login, g.Key.DisplayName,
                    g.Select(x => x.RoleName).OrderBy(x => x).ToList()))
                .OrderBy(m => m.DisplayName)
                .ToList(),
            roleAccess);
    }

    /// <summary>
    /// Thêm một người vào project với một vai trò.
    ///
    /// Ràng buộc cấp bậc dùng lại đúng quy tắc của RBAC toàn cục (BR-SEC-08): chỉ cấp được vai trò
    /// **thấp hơn** cấp của mình. Thiếu nó thì một manager tự cấp `admin` cho mình trong project
    /// của mình là xong — và vì admin là quyền toàn cục, đó là leo thang ra khỏi phạm vi project.
    /// </summary>
    public async Task AddMemberAsync(string slug, string login, string roleName, ClaimsPrincipal actor, CancellationToken ct)
    {
        var (project, user, role) = await ResolveAsync(slug, login, roleName, ct);
        await EnsureCanGrantAsync(role, actor, ct);

        if (await _db.ProjectMembers.AnyAsync(m => m.ProjectId == project.Id && m.UserId == user.Id && m.RoleId == role.Id, ct))
        {
            return; // Gọi lại với cùng bộ ba vẫn 204 — thao tác này là đặt trạng thái, không phải đếm.
        }

        _db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = project.Id, UserId = user.Id, RoleId = role.Id, AddedAt = _clock.GetUtcNow()
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Audit project.member.add actor={ActorId} project={Slug} user={Login} role={Role}",
            actor.GetUserId(), slug, login, roleName);
    }

    public async Task RemoveMemberAsync(string slug, string login, string roleName, ClaimsPrincipal actor, CancellationToken ct)
    {
        var (project, user, role) = await ResolveAsync(slug, login, roleName, ct);
        await EnsureCanGrantAsync(role, actor, ct);

        var rows = await _db.ProjectMembers
            .Where(m => m.ProjectId == project.Id && m.UserId == user.Id && m.RoleId == role.Id)
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        _db.ProjectMembers.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Audit project.member.remove actor={ActorId} project={Slug} user={Login} role={Role}",
            actor.GetUserId(), slug, login, roleName);
    }

    public async Task SetRoleAccessAsync(string slug, string roleName, bool granted, ClaimsPrincipal actor, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var role = await FindRoleAsync(roleName, ct);
        await EnsureCanGrantAsync(role, actor, ct);

        var existing = await _db.ProjectRoleAccess
            .Where(a => a.ProjectId == project.Id && a.RoleId == role.Id)
            .ToListAsync(ct);

        if (granted && existing.Count == 0)
        {
            _db.ProjectRoleAccess.Add(new ProjectRoleAccess
            {
                ProjectId = project.Id, RoleId = role.Id, AddedAt = _clock.GetUtcNow()
            });
        }
        else if (!granted && existing.Count > 0)
        {
            _db.ProjectRoleAccess.RemoveRange(existing);
        }
        else
        {
            return;
        }

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Audit project.role_access.{Action} actor={ActorId} project={Slug} role={Role}",
            granted ? "grant" : "revoke", actor.GetUserId(), slug, roleName);
    }

    public async Task<IReadOnlyList<UserSummary>> CandidatesAsync(string slug, string? search, CancellationToken ct)
    {
        await _q.ProjectAsync(slug, ct);

        var q = _db.Users.AsNoTracking().Where(u => u.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            q = q.Where(u => u.Login.Contains(term) || u.DisplayName.ToLower().Contains(term));
        }

        return await q.OrderBy(u => u.DisplayName).Take(50)
            .Select(u => new UserSummary(u.Id, u.Login, u.DisplayName))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Người tạo project trở thành thành viên với đúng các vai trò toàn cục của mình.
    ///
    /// Không có bước này thì tạo xong một project là không vào được nó — đúng nghĩa đen, vì kiểm
    /// quyền theo project không tìm thấy hàng nào. Admin không cần vì quyền toàn cục trùm lên.
    /// </summary>
    public async Task AddCreatorAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        var roleIds = await _db.UserRoles.Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToListAsync(ct);
        if (roleIds.Count == 0) return;

        var now = _clock.GetUtcNow();
        foreach (var roleId in roleIds)
        {
            _db.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = projectId, UserId = userId, RoleId = roleId, AddedAt = now
            });
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task<(Project Project, User User, Role Role)> ResolveAsync(
        string slug, string login, string roleName, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var normalized = login.Trim().TrimStart('@').ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Login == normalized, ct)
            ?? throw AppException.NotFound($"Không tìm thấy người dùng '{login}'.");
        var role = await FindRoleAsync(roleName, ct);
        return (project, user, role);
    }

    private async Task<Role> FindRoleAsync(string roleName, CancellationToken ct)
    {
        var name = roleName.Trim().ToLowerInvariant();
        return await _db.Roles.FirstOrDefaultAsync(r => r.Name == name, ct)
            ?? throw AppException.NotFound($"Không tìm thấy vai trò '{roleName}'.");
    }

    private async Task EnsureCanGrantAsync(Role role, ClaimsPrincipal actor, CancellationToken ct)
    {
        var actorId = actor.GetUserId();
        var level = await _db.UserRoles
            .Where(ur => ur.UserId == actorId)
            .Select(ur => (int?)ur.Role.Rank)
            .MaxAsync(ct) ?? 0;

        // Cấp toàn cục thấp thì vẫn có thể đang là manager của chính project này; lấy cấp cao
        // nhất giữa hai nguồn, nếu không manager sẽ không cấp được gì cho ai.
        var projectLevel = await _db.ProjectMembers
            .Where(m => m.UserId == actorId)
            .Select(m => (int?)m.Role.Rank)
            .MaxAsync(ct) ?? 0;

        var actorLevel = Math.Max(level, projectLevel);
        if (role.Rank >= actorLevel)
        {
            throw AppException.Forbidden(
                $"Vai trò '{role.Name}' ở cấp {role.Rank}, ngang hoặc cao hơn cấp {actorLevel} của bạn.");
        }
    }
}

[ApiController]
[Route("api/projects/{project}")]
[Produces("application/json")]
public sealed class ProjectMembersController : ControllerBase
{
    private readonly ProjectMemberService _members;

    public ProjectMembersController(ProjectMemberService members) => _members = members;

    /// <summary>Ai làm ở project này. Chỉ cần đọc được project là xem được danh sách.</summary>
    [HttpGet("members")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    [ProducesResponseType(typeof(ProjectAccessResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProjectAccessResponse>> List(string project, CancellationToken ct)
        => Ok(await _members.ListAsync(project, ct));

    /// <summary>
    /// Người có thể thêm vào project này.
    ///
    /// Manager cần tìm người để thêm, nhưng không được mở toàn bộ danh bạ nội bộ — nên đường tìm
    /// nằm **trong phạm vi project** và chỉ trả tên, login, ảnh đại diện. Không email, không vai
    /// trò, không trạng thái hoạt động của người khác.
    /// </summary>
    [HttpGet("member-candidates")]
    [RequirePermission(Permissions.ProjectMemberManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    [ProducesResponseType(typeof(IReadOnlyList<UserSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UserSummary>>> Candidates(
        string project, [FromQuery] string? search, CancellationToken ct)
        => Ok(await _members.CandidatesAsync(project, search, ct));

    [HttpPut("members/{login}/roles/{role}")]
    [RequirePermission(Permissions.ProjectMemberManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Add(string project, string login, string role, CancellationToken ct)
    {
        await _members.AddMemberAsync(project, login, role, User, ct);
        return NoContent();
    }

    [HttpDelete("members/{login}/roles/{role}")]
    [RequirePermission(Permissions.ProjectMemberManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(string project, string login, string role, CancellationToken ct)
    {
        await _members.RemoveMemberAsync(project, login, role, User, ct);
        return NoContent();
    }

    /// <summary>Cấp cho **cả vai trò** với tới project này — dùng cho khách hàng.</summary>
    [HttpPut("role-access/{role}")]
    [RequirePermission(Permissions.ProjectMemberManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> GrantRole(string project, string role, CancellationToken ct)
    {
        await _members.SetRoleAccessAsync(project, role, granted: true, User, ct);
        return NoContent();
    }

    [HttpDelete("role-access/{role}")]
    [RequirePermission(Permissions.ProjectMemberManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RevokeRole(string project, string role, CancellationToken ct)
    {
        await _members.SetRoleAccessAsync(project, role, granted: false, User, ct);
        return NoContent();
    }
}
