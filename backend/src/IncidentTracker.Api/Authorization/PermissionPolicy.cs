using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Authorization;

/// <summary>Tên claim mang permission trong JWT (ADR-001).</summary>
public static class AppClaimTypes
{
    /// <summary>
    /// Quyền **có hiệu lực cho request hiện tại** — đã gộp quyền toàn cục với quyền ở project
    /// trong đường dẫn. Xem <c>PrincipalEnrichmentMiddleware.EffectivePermissions</c>.
    /// </summary>
    public const string Permission = "perm";

    /// <summary>
    /// Quyền **toàn cục** — chỉ đến từ vai trò có <c>IsGlobal</c>.
    ///
    /// Cần riêng vì <see cref="Permission"/> ở endpoint không có <c>{project}</c> là hợp của mọi
    /// project người dùng với tới. Với màn hình xuyên project (Search, hộp thư) thì đúng: vào
    /// được màn hình, dữ liệu lọc sau. Nhưng với endpoint **quản trị toàn hệ thống** thì sai —
    /// một manager có <c>user.read</c> ở một project sẽ liệt kê được toàn bộ tài khoản.
    /// </summary>
    public const string GlobalPermission = "gperm";

    /// <summary>
    /// Quyền theo từng project, dạng <c>"{slug}:{code}"</c>.
    ///
    /// Khác <see cref="Permission"/> ở **câu hỏi nó trả lời**: <c>perm</c> trả lời "có được làm
    /// việc này không" (cho phép/từ chối), còn <c>pperm</c> trả lời "**ở những project nào**".
    /// Câu thứ hai chỉ những chỗ lọc dữ liệu xuyên project mới cần — Search, danh sách ticket
    /// chung, hộp thư, board — và chúng không thể suy ra từ <c>perm</c> vì ở endpoint không có
    /// <c>{project}</c>, <c>perm</c> đã bị gộp lại thành một tập phẳng.
    /// </summary>
    public const string ProjectPermission = "pperm";
    public const string Subject = "sub";
    public const string DisplayName = "name";
    public const string Email = "email";
    public const string Login = "login";
}

/// <summary>CMP-03 — requirement mang mã permission mà endpoint đòi hỏi.</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission, bool globalOnly = false)
    {
        Permission = permission;
        GlobalOnly = globalOnly;
    }

    public string Permission { get; }

    /// <summary>Chỉ chấp nhận quyền toàn cục, không chấp nhận quyền có được nhờ một project.</summary>
    public bool GlobalOnly { get; }
}

/// <summary>
/// CMP-03 Authorization Handler (FR-009, DRV-01).
/// So khớp permission bắt buộc của endpoint với claims đã xác thực — không truy vấn DB
/// trên read path theo ADR-001.
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    /// <summary>Khóa lưu permission bị thiếu để handler 403 nói rõ endpoint đòi quyền gì.</summary>
    public const string MissingPermissionKey = "__missingPermission";

    /// <summary>Tên tham số route mang slug của project.</summary>
    public const string ProjectRouteKey = "project";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public PermissionAuthorizationHandler(IHttpContextAccessor httpContextAccessor)
        => _httpContextAccessor = httpContextAccessor;

    /// <summary>
    /// <b>Hình dạng đường dẫn quyết định phạm vi kiểm quyền.</b>
    ///
    /// Có <c>{project}</c> trong route thì kiểm quyền **trên project đó**; không có thì kiểm
    /// quyền toàn cục như trước. Nhờ vậy 12 trên 24 controller — vốn đã nằm dưới
    /// <c>api/projects/{project}/…</c> — chuyển sang mô hình mới mà không phải sửa dòng nào trong
    /// 137 chỗ gắn <c>[RequirePermission]</c>.
    ///
    /// Cái giá là một endpoint thuộc project mà quên đặt <c>{project}</c> vào đường dẫn sẽ **âm
    /// thầm rơi về kiểm toàn cục**. Đó là loại lỗi không ai phát hiện cho tới khi muộn, nên có
    /// test canh gác liệt kê mọi endpoint và bắt mỗi cái tự khai thuộc nhóm nào
    /// (<c>EndpointScopeTests</c>).
    /// </summary>
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        // Không tự hỏi project nào: claim trên principal đã là **quyền hiệu lực cho request này**
        // (PrincipalEnrichmentMiddleware.EffectivePermissions). Nếu handler cũng tự tính lần nữa
        // thì có hai nơi cùng quyết định một điều, và chúng sẽ lệch nhau.
        var granted = requirement.GlobalOnly
            ? context.User.HasGlobalPermission(requirement.Permission)
            : context.User.HasPermission(requirement.Permission);

        if (granted)
        {
            context.Succeed(requirement);
        }
        else if (_httpContextAccessor.HttpContext is { } httpContext)
        {
            httpContext.Items[MissingPermissionKey] = requirement.Permission;
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Sinh policy theo quy ước "perm:{code}" nên thêm permission mới không phải đăng ký
/// thủ công từng policy trong Program.cs (NFR-MNT-01).
/// </summary>
public sealed class PermissionPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public const string Prefix = "perm:";

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : base(options) { }

    /// <summary>Tiền tố cho phiên bản chỉ nhận quyền toàn cục.</summary>
    public const string GlobalPrefix = "gperm:";

    /// <summary>Tiền tố cho phiên bản đòi **đủ** nhiều quyền, ngăn cách bằng dấu phẩy.</summary>
    public const string AllPrefix = "perms:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(GlobalPrefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName[GlobalPrefix.Length..], globalOnly: true))
                .Build();
        }

        // Đòi **đủ** nhiều quyền. Không cần sửa handler: một policy nhiều requirement thì
        // ASP.NET Core chỉ cho qua khi mọi requirement đều được đáp ứng, tức đúng phép AND.
        if (policyName.StartsWith(AllPrefix, StringComparison.Ordinal))
        {
            var builder = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();
            foreach (var code in policyName[AllPrefix.Length..].Split(','))
            {
                builder.AddRequirements(new PermissionRequirement(code));
            }
            return builder.Build();
        }

        if (policyName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(policyName[Prefix.Length..]))
                .Build();
        }

        return await base.GetPolicyAsync(policyName);
    }
}

/// <summary>
/// Khai báo permission ngay trên action. Quyết định 401/403 xảy ra trong authorization
/// middleware, trước khi request chạm controller (BR-05, US-005/AC-02..04).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequirePermissionAttribute : Microsoft.AspNetCore.Authorization.AuthorizeAttribute
{
    public RequirePermissionAttribute(string permission)
        => Policy = PermissionPolicyProvider.Prefix + permission;
}

/// <summary>
/// Đòi <b>đủ</b> các permission liệt kê, không phải một trong số đó.
///
/// Dùng khi hai quyền vốn tách rời nhưng một màn hình gộp chúng lại: người không sửa được thứ
/// này thì cũng không được sửa thứ kia, và luật đó phải có ở API chứ không chỉ ở chỗ ẩn nút —
/// ẩn nút chỉ giấu đường đi, không đóng nó.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireAllPermissionsAttribute : Microsoft.AspNetCore.Authorization.AuthorizeAttribute
{
    public RequireAllPermissionsAttribute(params string[] permissions)
        => Policy = PermissionPolicyProvider.AllPrefix + string.Join(',', permissions);
}

/// <summary>
/// Như <see cref="RequirePermissionAttribute"/> nhưng **chỉ nhận quyền toàn cục**.
///
/// Dùng cho endpoint quản trị toàn hệ thống — danh sách tài khoản, RBAC, cấu hình SLA. Với
/// <see cref="RequirePermissionAttribute"/> thường, một quyền có được nhờ làm việc ở một project
/// cũng mở được chúng: manager có <c>user.read</c> trong project của mình sẽ đọc được **toàn bộ**
/// danh bạ. Đó là leo thang ra khỏi phạm vi project qua đường vòng.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireGlobalPermissionAttribute : Microsoft.AspNetCore.Authorization.AuthorizeAttribute
{
    public RequireGlobalPermissionAttribute(string permission)
        => Policy = PermissionPolicyProvider.GlobalPrefix + permission;
}

public static class ClaimsPrincipalExtensions
{
    public static bool HasPermission(this System.Security.Claims.ClaimsPrincipal user, string permission)
        => user.Claims.Any(c => c.Type == AppClaimTypes.Permission
                                && string.Equals(c.Value, permission, StringComparison.Ordinal));

    /// <summary>Quyền có được từ vai trò toàn cục, không tính quyền nhờ một project nào.</summary>
    public static bool HasGlobalPermission(this System.Security.Claims.ClaimsPrincipal user, string permission)
        => user.Claims.Any(c => c.Type == AppClaimTypes.GlobalPermission
                                && string.Equals(c.Value, permission, StringComparison.Ordinal));

    /// <summary>
    /// Slug của những project mà người này có <paramref name="permission"/>.
    ///
    /// Trả <c>null</c> nghĩa là **mọi project** — người dùng có quyền toàn cục, không cần lọc.
    /// Trả danh sách rỗng nghĩa là **không project nào**, và chỗ gọi phải trả về tập rỗng chứ
    /// không phải bỏ qua bước lọc. Nhầm hai trường hợp này là để lộ toàn bộ dữ liệu.
    /// </summary>
    public static IReadOnlyList<string>? ProjectsWith(
        this System.Security.Claims.ClaimsPrincipal user, string permission)
    {
        if (user.HasGlobalPermission(permission))
        {
            return null;
        }

        var suffix = ":" + permission;
        return user.Claims
            .Where(c => c.Type == AppClaimTypes.ProjectPermission && c.Value.EndsWith(suffix, StringComparison.Ordinal))
            .Select(c => c.Value[..^suffix.Length])
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }


    /// <summary>Lấy actor id từ claim <c>sub</c> — không bao giờ nhận từ body (BR-BIZ-04).</summary>
    public static Guid GetUserId(this System.Security.Claims.ClaimsPrincipal user)
    {
        var raw = user.FindFirst(AppClaimTypes.Subject)?.Value
                  ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id)
            ? id
            : throw new InvalidOperationException("Token hợp lệ nhưng thiếu claim 'sub'.");
    }
}
