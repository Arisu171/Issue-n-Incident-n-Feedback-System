using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Identity;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Rbac;

/// <summary>
/// CMP-04 RBAC Application Service — giữ invariant BR-01..BR-03 và điều phối use case
/// UC-01..UC-05. Controller không chứa logic nghiệp vụ.
/// </summary>
public sealed class RbacService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly ILogger<RbacService> _logger;

    public RbacService(AppDbContext db, IPasswordHasher<User> hasher, ILogger<RbacService> logger)
    {
        _db = db;
        _hasher = hasher;
        _logger = logger;
    }

    // ---------------- Hàng rào thứ bậc (BR-SEC-08) ----------------
    //
    // Mỗi role mang một `rank`; cấp của một user là rank cao nhất trong các role họ có.
    // Hai bất biến, kiểm ở Application chứ không ở Controller vì controller chỉ biết
    // "có permission hay không", còn ở đây mới biết actor đang đứng ở đâu so với mục tiêu:
    //
    //   1. Không ai tác động lên chính mình qua các endpoint quản trị (tự gán role, tự khoá).
    //   2. Chỉ tác động được lên user có cấp THẤP HƠN, và chỉ cấp được role rank thấp hơn cấp mình.
    //
    // Thiếu hai chốt này thì bất kỳ ai giữ `user.role.assign` đều tự nâng lên admin bằng đúng
    // một request, và hai admin có thể vô hiệu hoá lẫn nhau.

    private async Task<int> LevelOfAsync(Guid userId, CancellationToken ct)
        => await _db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => (int?)ur.Role.Rank)
            .MaxAsync(ct) ?? 0;

    private static int LevelOf(User user)
        => user.UserRoles.Count == 0 ? 0 : user.UserRoles.Max(ur => ur.Role.Rank);

    private async Task EnsureCanManageUserAsync(Guid targetId, Guid actorId, string action, CancellationToken ct)
    {
        if (targetId == actorId)
        {
            throw AppException.Forbidden(
                $"Không thể tự {action} trên tài khoản của chính mình — "
                + "đây là hàng rào chống tự nâng quyền (BR-SEC-08).");
        }

        var actorLevel = await LevelOfAsync(actorId, ct);
        var targetLevel = await LevelOfAsync(targetId, ct);

        if (targetLevel >= actorLevel)
        {
            throw AppException.Forbidden(
                $"Tài khoản đích ở cấp {targetLevel}, ngang hoặc cao hơn cấp {actorLevel} của bạn.");
        }
    }

    /// <summary>
    /// Role đem cấp/gỡ không được CAO HƠN cấp của người thao tác. Cố ý cho phép cấp role
    /// ngang cấp mình: nếu cấm, đội admin không bao giờ kết nạp thêm được ai vì "admin" là
    /// role cao nhất và mọi admin đều ngang nó. Cấp ngang không tạo ra leo thang — người
    /// được nâng chỉ bằng chứ không vượt người nâng, và từ lúc đó hai bên là đồng cấp nên
    /// không ai đụng được vào ai.
    /// </summary>
    private async Task<Role> EnsureRoleNotAboveActorAsync(Guid roleId, Guid actorId, CancellationToken ct)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy role '{roleId}'.");

        var actorLevel = await LevelOfAsync(actorId, ct);
        if (role.Rank > actorLevel)
        {
            throw AppException.Forbidden(
                $"Role '{role.Name}' ở cấp {role.Rank}, cao hơn cấp {actorLevel} của bạn.");
        }

        return role;
    }

    /// <summary>
    /// Sửa cấu hình của một role. Ngoài luật cấp, còn cấm đụng vào role mà chính actor đang
    /// mang — nếu không thì chỉ cần thêm permission vào role của mình là tự nâng quyền xong.
    /// </summary>
    private async Task<Role> EnsureCanEditRoleAsync(Guid roleId, Guid actorId, CancellationToken ct)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy role '{roleId}'.");

        if (await _db.UserRoles.AnyAsync(ur => ur.UserId == actorId && ur.RoleId == roleId, ct))
        {
            throw AppException.Forbidden(
                $"Không thể sửa role '{role.Name}' vì chính bạn đang mang role này.");
        }

        var actorLevel = await LevelOfAsync(actorId, ct);
        if (role.Rank >= actorLevel)
        {
            throw AppException.Forbidden(
                $"Role '{role.Name}' ở cấp {role.Rank}, ngang hoặc cao hơn cấp {actorLevel} của bạn.");
        }

        return role;
    }

    private Task<bool> IsActiveAdminAsync(Guid userId, CancellationToken ct)
        => _db.UserRoles.AnyAsync(
            ur => ur.UserId == userId && ur.Role.Name == Permissions.AdminRole && ur.User.IsActive, ct);

    /// <summary>Hệ thống phải luôn còn ít nhất một admin đang hoạt động.</summary>
    private async Task EnsureAdminRemainsAsync(Guid losingUserId, CancellationToken ct)
    {
        var remaining = await _db.UserRoles.CountAsync(
            ur => ur.Role.Name == Permissions.AdminRole && ur.User.IsActive && ur.UserId != losingUserId, ct);

        if (remaining == 0)
        {
            throw AppException.Conflict(
                "Đây là quản trị viên đang hoạt động cuối cùng; thao tác này sẽ khiến "
                + "không còn ai cấu hình được RBAC nữa.");
        }
    }

    // ---------------- User (UC-01) ----------------

    public async Task<UserResponse> CreateUserAsync(CreateUserRequest request, Guid actorId, CancellationToken ct)
    {
        var email = IdentityService.NormalizeEmail(request.Email);

        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
        {
            throw AppException.Conflict($"Email '{email}' đã được sử dụng.");
        }

        var roles = await ResolveRolesAsync(request.Roles, ct);

        var actorLevel = await LevelOfAsync(actorId, ct);
        var tooHigh = roles.Where(r => r.Rank > actorLevel).Select(r => r.Name).OrderBy(x => x).ToList();
        if (tooHigh.Count > 0)
        {
            throw AppException.Forbidden(
                $"Không thể cấp role {string.Join(", ", tooHigh)} vì cao hơn cấp {actorLevel} của bạn.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Login = await LoginNames.EnsureUniqueAsync(_db, LoginNames.FromEmail(email), ct),
            DisplayName = request.DisplayName.Trim(),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        user.PasswordHash = _hasher.HashPassword(user, request.Password);
        user.UserRoles = roles.Select(r => new UserRole { UserId = user.Id, RoleId = r.Id }).ToList();

        _db.Users.Add(user);
        // Unique index mới là thứ thực sự bảo đảm BR-01; kiểm tra ở trên chỉ để có thông báo đẹp.
        await _db.SaveTranslatingConflictAsync($"Email '{email}' đã được sử dụng.", ct);

        // FR-011: audit hành động quản trị, không kèm password hay hash.
        _logger.LogInformation("Audit rbac.user.create actor={ActorId} target={TargetId} roles={Roles} result=success",
            actorId, user.Id, string.Join(',', roles.Select(r => r.Name)));

        return ToResponse(user, roles.Select(r => r.Name).OrderBy(x => x).ToList(),
            roles.Count == 0 ? 0 : roles.Max(r => r.Rank));
    }

    public async Task<UserResponse> UpdateUserAsync(Guid id, UpdateUserRequest request, Guid actorId, CancellationToken ct)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy user '{id}'.");

        await EnsureCanManageUserAsync(id, actorId, "cập nhật", ct);

        // Khoá admin cuối cùng là khoá cửa hệ thống từ bên trong, kể cả khi luật cấp cho phép.
        if (request.IsActive is false && await IsActiveAdminAsync(id, ct))
        {
            await EnsureAdminRemainsAsync(id, ct);
        }

        if (request.DisplayName is not null)
        {
            user.DisplayName = request.DisplayName.Trim();
        }

        if (request.IsActive is not null)
        {
            user.IsActive = request.IsActive.Value;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit rbac.user.update actor={ActorId} target={TargetId} isActive={IsActive} result=success",
            actorId, user.Id, user.IsActive);

        return ToResponse(user, user.UserRoles.Select(ur => ur.Role.Name).OrderBy(x => x).ToList(), LevelOf(user));
    }

    public async Task<PagedResult<UserResponse>> ListUsersAsync(UserListQuery query, CancellationToken ct)
    {
        var q = _db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = $"%{query.Search.Trim()}%";
            q = q.Where(u => EF.Functions.ILike(u.Email, term) || EF.Functions.ILike(u.DisplayName, term) || EF.Functions.ILike(u.Login, term));
        }

        if (query.IsActive is not null)
        {
            q = q.Where(u => u.IsActive == query.IsActive.Value);
        }

        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(u => u.Email)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<UserResponse>(
            items.Select(u => ToResponse(u, u.UserRoles.Select(ur => ur.Role.Name).OrderBy(x => x).ToList(), LevelOf(u))).ToList(),
            query.Page, query.PageSize, total);
    }

    /// <summary>FR-005 — đọc user kèm danh sách role.</summary>
    public async Task<UserResponse> GetUserAsync(Guid id, CancellationToken ct)
    {
        var user = await _db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy user '{id}'.");

        return ToResponse(user, user.UserRoles.Select(ur => ur.Role.Name).OrderBy(x => x).ToList(), LevelOf(user));
    }

    /// <summary>
    /// UC-01 · <c>DELETE /api/users/{id}</c>. Idempotent: gọi lại trên id đã xóa vẫn 204.
    ///
    /// Ba hàng rào trước khi xóa, theo thứ tự rủi ro giảm dần: không tự xóa mình (mất phiên
    /// ngay giữa thao tác), không xóa admin cuối cùng (khóa cửa hệ thống từ bên trong), và
    /// không xóa tài khoản đã để lại dấu vết nghiệp vụ. Hàng rào thứ ba không chỉ để lịch sự
    /// với FK: <c>incident_status_history.changed_by</c> là bằng chứng ai đã làm gì
    /// (GOAL-BIZ-02) — xóa người là xóa luôn khả năng truy vết. Muốn chặn truy cập thì dùng
    /// <c>isActive = false</c>.
    /// </summary>
    public async Task DeleteUserAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null)
        {
            return;
        }

        await EnsureCanManageUserAsync(id, actorId, "xóa", ct);

        if (await IsActiveAdminAsync(id, ct))
        {
            await EnsureAdminRemainsAsync(id, ct);
        }

        var blockers = new List<string>();

        if (await _db.Incidents.IgnoreQueryFilters().AnyAsync(i => i.ReporterId == id, ct))
        {
            blockers.Add("đã báo cáo sự cố");
        }

        if (await _db.IncidentStatusHistory.AnyAsync(h => h.ChangedBy == id, ct))
        {
            blockers.Add("đã chuyển trạng thái sự cố");
        }

        if (await _db.Feedbacks.AnyAsync(f => f.CreatedBy == id, ct))
        {
            blockers.Add("đã ghi nhận phản hồi");
        }

        if (await _db.IncidentComments.AnyAsync(c => c.AuthorId == id, ct))
        {
            blockers.Add("đã trao đổi trên sự cố");
        }

        if (await _db.FeedbackReplies.AnyAsync(r => r.ResponderId == id, ct))
        {
            blockers.Add("đã trả lời phản hồi");
        }

        // Chữ ký trong lịch sử sửa đổi cũng là dữ liệu nghiệp vụ, và khóa ngoại của nó là
        // Restrict. Thiếu dòng này thì xóa một tài khoản từng sửa bài sẽ vỡ ở tầng DB — 500
        // kèm thông báo của Postgres thay vì 409 kèm lý do đọc được.
        if (await _db.ContentRevisions.AnyAsync(r => r.EditedBy == id, ct))
        {
            blockers.Add("đã sửa nội dung sự cố hoặc phản hồi");
        }

        if (blockers.Count > 0)
        {
            throw AppException.Conflict(
                $"User '{user.Email}' {string.Join(", ", blockers)} nên không xóa được. "
                + "Dùng PATCH /api/users/{id} với isActive=false để vô hiệu hóa thay vì xóa.",
                new Dictionary<string, object?> { ["blockedBy"] = blockers });
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit rbac.user.delete actor={ActorId} target={TargetId} result=success",
            actorId, id);
    }

    // ---------------- Gán quan hệ (UC-04, UC-05) ----------------

    /// <summary>FR-003 · API-Role-Assign — idempotent, composite PK chặn cặp trùng (BR-03).</summary>
    public async Task AssignRoleAsync(Guid userId, Guid roleId, Guid actorId, CancellationToken ct)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == userId, ct))
        {
            throw AppException.NotFound($"Không tìm thấy user '{userId}'.");
        }

        await EnsureCanManageUserAsync(userId, actorId, "gán role", ct);
        await EnsureRoleNotAboveActorAsync(roleId, actorId, ct);

        if (await _db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId, ct))
        {
            return; // Gọi lại vẫn 204, không tạo bản ghi thứ hai.
        }

        _db.UserRoles.Add(new UserRole { UserId = userId, RoleId = roleId });
        // Composite PK chặn cặp trùng (BR-03); hai request song song thì request thua vẫn 204.
        await _db.SaveIgnoringDuplicateAsync(ct);

        _logger.LogInformation("Audit rbac.user.role.assign actor={ActorId} user={UserId} role={RoleId} result=success",
            actorId, userId, roleId);
    }

    public async Task RemoveRoleAsync(Guid userId, Guid roleId, Guid actorId, CancellationToken ct)
    {
        await EnsureCanManageUserAsync(userId, actorId, "gỡ role", ct);
        var role = await EnsureRoleNotAboveActorAsync(roleId, actorId, ct);

        var link = await _db.UserRoles.FirstOrDefaultAsync(ur => ur.UserId == userId && ur.RoleId == roleId, ct);
        if (link is null)
        {
            return; // Idempotent.
        }

        if (role.Name == Permissions.AdminRole && await IsActiveAdminAsync(userId, ct))
        {
            await EnsureAdminRemainsAsync(userId, ct);
        }

        _db.UserRoles.Remove(link);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit rbac.user.role.remove actor={ActorId} user={UserId} role={RoleId} result=success",
            actorId, userId, roleId);
    }

    /// <summary>FR-004 · API-Perm-Assign.</summary>
    public async Task AssignPermissionAsync(Guid roleId, Guid permissionId, Guid actorId, CancellationToken ct)
    {
        await EnsureCanEditRoleAsync(roleId, actorId, ct);

        if (!await _db.Permissions.AnyAsync(p => p.Id == permissionId, ct))
        {
            throw AppException.NotFound($"Không tìm thấy permission '{permissionId}'.");
        }

        if (await _db.RolePermissions.AnyAsync(rp => rp.RoleId == roleId && rp.PermissionId == permissionId, ct))
        {
            return;
        }

        _db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = permissionId });
        await _db.SaveIgnoringDuplicateAsync(ct);

        _logger.LogInformation("Audit rbac.role.permission.assign actor={ActorId} role={RoleId} permission={PermissionId} result=success",
            actorId, roleId, permissionId);
    }

    public async Task RemovePermissionAsync(Guid roleId, Guid permissionId, Guid actorId, CancellationToken ct)
    {
        await EnsureCanEditRoleAsync(roleId, actorId, ct);

        var link = await _db.RolePermissions
            .FirstOrDefaultAsync(rp => rp.RoleId == roleId && rp.PermissionId == permissionId, ct);
        if (link is null)
        {
            return;
        }

        _db.RolePermissions.Remove(link);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit rbac.role.permission.remove actor={ActorId} role={RoleId} permission={PermissionId} result=success",
            actorId, roleId, permissionId);
    }

    // ---------------- Role (UC-02) ----------------

    public async Task<RoleResponse> CreateRoleAsync(CreateRoleRequest request, Guid actorId, CancellationToken ct)
    {
        var name = request.Name.Trim().ToLowerInvariant();

        if (await _db.Roles.AnyAsync(r => r.Name == name, ct))
        {
            throw AppException.Conflict($"Role '{name}' đã tồn tại.");
        }

        // Tên của vai trò đã bỏ là tên dành riêng: bước gỡ lúc khởi động tìm theo tên, nên một
        // vai trò mới trùng tên sẽ bị xoá ở lần khởi động kế tiếp cùng với việc dồn người dùng
        // sang vai trò thay thế. Chặn ngay lúc tạo thì không bao giờ tới nước đó.
        if (Permissions.RetiredRoles.TryGetValue(name, out var replacedBy))
        {
            throw AppException.Conflict(
                $"'{name}' là tên của một vai trò đã bỏ, không dùng lại được. Vai trò thay thế là '{replacedBy}'.");
        }

        var actorLevel = await LevelOfAsync(actorId, ct);
        if (request.Rank >= actorLevel)
        {
            throw AppException.Forbidden(
                $"Không tạo được role ở cấp {request.Rank} vì ngang hoặc cao hơn cấp {actorLevel} của bạn.");
        }

        var role = new Role
        {
            Id = Guid.NewGuid(), Name = name, Description = request.Description, Rank = request.Rank
        };
        _db.Roles.Add(role);
        await _db.SaveTranslatingConflictAsync($"Role '{name}' đã tồn tại.", ct);

        _logger.LogInformation("Audit rbac.role.create actor={ActorId} role={RoleId} result=success", actorId, role.Id);

        return new RoleResponse(role.Id, role.Name, role.Description, role.Rank, Array.Empty<string>());
    }

    public async Task<RoleResponse> UpdateRoleAsync(Guid id, UpdateRoleRequest request, Guid actorId, CancellationToken ct)
    {
        await EnsureCanEditRoleAsync(id, actorId, ct);

        var role = await _db.Roles
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy role '{id}'.");

        role.Description = request.Description;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit rbac.role.update actor={ActorId} role={RoleId} result=success", actorId, role.Id);

        return ToResponse(role);
    }

    public async Task DeleteRoleAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        if (!await _db.Roles.AnyAsync(r => r.Id == id, ct))
        {
            return; // Idempotent.
        }

        var role = await EnsureCanEditRoleAsync(id, actorId, ct);

        if (await _db.UserRoles.AnyAsync(ur => ur.RoleId == id, ct))
        {
            throw AppException.Conflict(
                $"Role '{role.Name}' vẫn đang được gán cho ít nhất một user; gỡ hết trước khi xóa.");
        }

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit rbac.role.delete actor={ActorId} role={RoleId} result=success", actorId, id);
    }

    public async Task<IReadOnlyList<RoleResponse>> ListRolesAsync(CancellationToken ct)
    {
        var roles = await _db.Roles.AsNoTracking()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        return roles.Select(ToResponse).ToList();
    }

    public async Task<RoleResponse> GetRoleAsync(Guid id, CancellationToken ct)
    {
        var role = await _db.Roles.AsNoTracking()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy role '{id}'.");

        return ToResponse(role);
    }

    // ---------------- Permission (UC-03) ----------------

    public async Task<PermissionResponse> CreatePermissionAsync(
        CreatePermissionRequest request, Guid actorId, CancellationToken ct)
    {
        var code = request.Code.Trim().ToLowerInvariant();

        if (await _db.Permissions.AnyAsync(p => p.Code == code, ct))
        {
            throw AppException.Conflict($"Permission '{code}' đã tồn tại.");
        }

        var permission = new Permission { Id = Guid.NewGuid(), Code = code, Description = request.Description };
        _db.Permissions.Add(permission);
        await _db.SaveTranslatingConflictAsync($"Permission '{code}' đã tồn tại.", ct);

        _logger.LogInformation("Audit rbac.permission.create actor={ActorId} permission={PermissionId} code={Code} result=success",
            actorId, permission.Id, code);

        return new PermissionResponse(permission.Id, permission.Code, permission.Description);
    }

    public async Task DeletePermissionAsync(Guid id, Guid actorId, CancellationToken ct)
    {
        var permission = await _db.Permissions.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (permission is null)
        {
            return;
        }

        if (await _db.RolePermissions.AnyAsync(rp => rp.PermissionId == id, ct))
        {
            throw AppException.Conflict(
                $"Permission '{permission.Code}' vẫn đang thuộc ít nhất một role; gỡ hết trước khi xóa.");
        }

        _db.Permissions.Remove(permission);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit rbac.permission.delete actor={ActorId} permission={PermissionId} result=success",
            actorId, id);
    }

    /// <summary>UC-03 · <c>GET /api/permissions/{id}</c>.</summary>
    public async Task<PermissionResponse> GetPermissionAsync(Guid id, CancellationToken ct)
    {
        var permission = await _db.Permissions.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy permission '{id}'.");

        return new PermissionResponse(permission.Id, permission.Code, permission.Description);
    }

    /// <summary>UC-03 · <c>PATCH /api/permissions/{id}</c> — chỉ đổi mô tả, xem DTO.</summary>
    public async Task<PermissionResponse> UpdatePermissionAsync(
        Guid id, UpdatePermissionRequest request, Guid actorId, CancellationToken ct)
    {
        var permission = await _db.Permissions.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy permission '{id}'.");

        if (request.Description is not null)
        {
            permission.Description = string.IsNullOrWhiteSpace(request.Description)
                ? null
                : request.Description.Trim();
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit rbac.permission.update actor={ActorId} permission={PermissionId} result=success",
            actorId, id);

        return new PermissionResponse(permission.Id, permission.Code, permission.Description);
    }

    public async Task<IReadOnlyList<PermissionResponse>> ListPermissionsAsync(CancellationToken ct)
        => await _db.Permissions.AsNoTracking()
            .OrderBy(p => p.Code)
            .Select(p => new PermissionResponse(p.Id, p.Code, p.Description))
            .ToListAsync(ct);

    // ---------------- Helpers ----------------

    private async Task<List<Role>> ResolveRolesAsync(IReadOnlyCollection<string> names, CancellationToken ct)
    {
        if (names.Count == 0)
        {
            return new List<Role>();
        }

        var wanted = names.Select(n => n.Trim().ToLowerInvariant()).Distinct().ToList();
        var roles = await _db.Roles.Where(r => wanted.Contains(r.Name)).ToListAsync(ct);

        var missing = wanted.Except(roles.Select(r => r.Name)).ToList();
        if (missing.Count > 0)
        {
            throw AppException.BadRequest($"Role không tồn tại: {string.Join(", ", missing)}.");
        }

        return roles;
    }

    private static UserResponse ToResponse(User user, IReadOnlyList<string> roles, int level)
        => new(user.Id, user.Email, user.Login, user.DisplayName, user.IsActive, user.CreatedAt, roles, level);

    private static RoleResponse ToResponse(Role role)
        => new(role.Id, role.Name, role.Description, role.Rank,
            role.RolePermissions.Select(rp => rp.Permission.Code).OrderBy(x => x).ToList());
}
