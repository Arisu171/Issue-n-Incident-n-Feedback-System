using System.ComponentModel.DataAnnotations;
using IncidentTracker.Api.Common;

namespace IncidentTracker.Api.Modules.Rbac;

// ---------- User ----------

public sealed class CreateUserRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [Required, MaxLength(150), MinLength(1)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Chưa có chuẩn nào trong tài liệu; nhóm chốt tối thiểu 8 ký tự (giả định C8).</summary>
    [Required, MinLength(8), MaxLength(200)]
    public string Password { get; set; } = string.Empty;

    /// <summary>Tên role gán ngay khi tạo, ví dụ ["support"]. Bỏ trống thì user chưa có quyền nào.</summary>
    public List<string> Roles { get; set; } = new();
}

public sealed class UpdateUserRequest
{
    [MaxLength(150), MinLength(1)]
    public string? DisplayName { get; set; }

    public bool? IsActive { get; set; }
}

/// <summary>FR-010 — DTO allowlist, tuyệt đối không mang <c>password_hash</c>.</summary>
public sealed record UserResponse(
    Guid Id,
    string Email,
    string Login,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Roles,
    /// <summary>Cấp của user = rank cao nhất trong các role; 0 khi chưa có role nào.</summary>
    int Level);

public sealed class UserListQuery : PagingQuery
{
    /// <summary>Lọc theo email hoặc tên hiển thị (khớp một phần, không phân biệt hoa thường).</summary>
    [MaxLength(320)]
    public string? Search { get; set; }

    public bool? IsActive { get; set; }
}

// ---------- Role ----------

public sealed class CreateRoleRequest
{
    [Required, MaxLength(100), MinLength(1)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>
    /// Cấp của role, bắt buộc thấp hơn cấp của người tạo (BR-SEC-08) — nếu không thì tạo một
    /// role ngang admin rồi tự gán cho mình là xong.
    /// </summary>
    [Range(1, 999)]
    public int Rank { get; set; } = 10;
}

public sealed class UpdateRoleRequest
{
    [MaxLength(500)]
    public string? Description { get; set; }
}

public sealed record RoleResponse(
    Guid Id,
    string Name,
    string? Description,
    int Rank,
    IReadOnlyList<string> Permissions);

// ---------- Permission ----------

public sealed class CreatePermissionRequest
{
    /// <summary>BR-02 — bắt buộc theo mẫu <c>resource.action</c>.</summary>
    [Required, MaxLength(150)]
    [RegularExpression("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$",
        ErrorMessage = "code phải theo mẫu resource.action, ví dụ 'incident.resolve'.")]
    public string Code { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }
}

/// <summary>
/// Cố ý KHÔNG cho sửa <c>rank</c> sau khi tạo: nâng cấp một role đang có người mang sẽ nâng
/// luôn cấp của họ, và người vừa được nâng có thể vượt lên trên chính người đã sửa.
/// </summary>
/// <summary>
/// Chỉ sửa được mô tả. <c>code</c> là định danh nghiệp vụ đã nằm trong policy của endpoint và
/// trong token đang lưu hành, nên đổi nó tại chỗ sẽ âm thầm tước quyền của người đang dùng —
/// muốn đổi thì tạo code mới rồi gỡ code cũ.
/// </summary>
public sealed class UpdatePermissionRequest
{
    [MaxLength(500)]
    public string? Description { get; set; }
}

public sealed record PermissionResponse(Guid Id, string Code, string? Description);
