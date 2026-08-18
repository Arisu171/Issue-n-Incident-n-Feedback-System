using System.Security.Claims;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Authorization;

/// <summary>Cấu hình cho việc nạp quyền theo request (ADR-004).</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Thời gian nhớ tạm quyền của một user, tính bằng giây. <c>0</c> nghĩa là đọc thẳng
    /// database mỗi request — thu hồi quyền có hiệu lực tức thì.
    ///
    /// Đặt lớn hơn 0 sẽ đổi độ trễ thu hồi lấy số lượt truy vấn: quyền vừa gỡ vẫn còn hiệu
    /// lực tối đa bằng đúng khoảng này. Chỉ nên bật khi đã đo được rằng truy vấn quyền là
    /// nút thắt thật, chứ không phải vì cảm giác.
    /// </summary>
    public int PermissionCacheSeconds { get; set; }
}

/// <summary>
/// Nạp vai trò và permission từ database vào principal của request hiện tại.
///
/// <para><b>Vì sao không để trong token.</b> Nhét permission vào JWT khiến token phình to và
/// bị gửi lại trên mọi request. Nặng hơn: payload JWT chỉ là base64, ai cầm token cũng đọc
/// được, nên nó phơi ra toàn bộ danh mục năng lực của hệ thống — một tấm bản đồ chỉ đường cho
/// người muốn dò tìm. Token vì vậy chỉ mang những gì <b>không</b> tra ngược được từ user id:
/// <c>sub</c>, <c>jti</c>, <c>iss</c>, <c>aud</c>, <c>exp</c>. Mọi thứ khác đọc từ database
/// tại thời điểm dùng.</para>
///
/// <para><b>Cái được lớn nhất lại không phải hai điều trên.</b> Quyền đọc lúc dùng nghĩa là
/// gỡ quyền có hiệu lực ngay ở request kế tiếp. Với mô hình cũ, một tài khoản bị thu hồi
/// quyền hoặc bị vô hiệu hóa vẫn thao tác được cho tới khi token hết hạn.</para>
///
/// <para>Chạy sau <c>UseAuthentication</c> và trước <c>UseAuthorization</c>: lúc này danh tính
/// đã được xác thực nhưng chưa ai hỏi tới quyền.</para>
/// </summary>
public sealed class PrincipalEnrichmentMiddleware
{
    private readonly RequestDelegate _next;

    public PrincipalEnrichmentMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        AppDbContext db,
        IMemoryCache cache,
        IOptionsMonitor<AuthOptions> options,
        ILogger<PrincipalEnrichmentMiddleware> logger)
    {
        // Chưa xác thực thì không có gì để nạp; authorization sẽ tự trả 401.
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        // Endpoint công khai không cần quyền. Bỏ qua để một token cũ đính kèm nhầm vào
        // /api/auth/login hay /api/health không làm hỏng chính đường đăng nhập lại.
        if (context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await _next(context);
            return;
        }

        if (!TryGetUserId(context.User, out var userId))
        {
            logger.LogWarning("Token hợp lệ nhưng thiếu hoặc sai định dạng claim 'sub'.");
            await ProblemDetailsWriter.WriteUnauthorizedAsync(context);
            return;
        }

        var snapshot = await LoadAsync(userId, db, cache, options.CurrentValue, context.RequestAborted);

        // Tài khoản đã bị xóa hoặc vô hiệu hóa sau khi token được phát: từ chối ngay, không
        // chờ token hết hạn. Đây chính là điều mô hình nhét quyền vào token không làm được.
        if (snapshot is null || !snapshot.IsActive)
        {
            logger.LogWarning("Từ chối user {UserId}: tài khoản không còn hiệu lực.", userId);
            await ProblemDetailsWriter.WriteUnauthorizedAsync(context);
            return;
        }

        // Đổi mật khẩu là giết mọi phiên khác. Token phát trước mốc đổi bị từ chối ngay tại đây —
        // rẻ hơn danh sách thu hồi theo `jti`, và middleware này vốn đã đọc bảng users mỗi
        // request nên không tốn thêm truy vấn nào.
        if (snapshot.PasswordChangedAt is { } changedAt && IssuedBefore(context.User, changedAt))
        {
            logger.LogWarning("Từ chối user {UserId}: token phát trước lần đổi mật khẩu.", userId);
            await ProblemDetailsWriter.WriteUnauthorizedAsync(context);
            return;
        }

        if (context.User.Identity is ClaimsIdentity identity)
        {
            identity.AddClaim(new Claim(AppClaimTypes.Email, snapshot.Email));
            identity.AddClaim(new Claim(AppClaimTypes.Login, snapshot.Login));
            identity.AddClaim(new Claim(AppClaimTypes.DisplayName, snapshot.DisplayName));
            identity.AddClaims(snapshot.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
            identity.AddClaims(EffectivePermissions(context, snapshot)
                .Select(p => new Claim(AppClaimTypes.Permission, p)));
            identity.AddClaims(snapshot.Permissions
                .Select(p => new Claim(AppClaimTypes.GlobalPermission, p)));
            // Bản đồ project → quyền, cho những chỗ phải lọc dữ liệu xuyên project.
            identity.AddClaims(snapshot.ProjectPermissions
                .SelectMany(kv => kv.Value.Select(code =>
                    new Claim(AppClaimTypes.ProjectPermission, $"{kv.Key}:{code}"))));
        }

        await _next(context);
    }

    /// <summary>
    /// Quyền có hiệu lực cho **request này**.
    ///
    /// <para><b>Hình dạng đường dẫn quyết định phạm vi.</b> Có <c>{project}</c> trong route thì
    /// quyền = toàn cục ∪ quyền ở đúng project đó. Không có thì quyền = toàn cục ∪ hợp của mọi
    /// project người này với tới.</para>
    ///
    /// <para>Tính ở đây, một lần, thay vì bắt từng chỗ tự hỏi "project nào": nhờ vậy 137 chỗ gắn
    /// <c>[RequirePermission]</c> và 19 chỗ gọi <c>HasPermission</c> trong service tự trở thành
    /// project-aware mà không phải sửa dòng nào. Sửa 156 chỗ bằng tay thì vừa lâu vừa chắc chắn
    /// sót, và chỗ sót là một lỗ thủng im lặng.</para>
    ///
    /// <para><b>Nhánh "không có project" là chỗ phải cẩn thận.</b> Nó chỉ mở được **màn hình**,
    /// không mở dữ liệu: Search, danh sách ticket xuyên project và hộp thư vẫn phải tự cắt kết
    /// quả theo project mà người dùng với tới. Nếu không thì chỉ cần gõ vào ô tìm kiếm là đọc
    /// được ticket của project mình không có quyền, và toàn bộ việc cách ly thành vô nghĩa.</para>
    /// </summary>
    private static IEnumerable<string> EffectivePermissions(HttpContext context, UserAuthSnapshot snapshot)
    {
        var project = context.Request.RouteValues
            .TryGetValue(PermissionAuthorizationHandler.ProjectRouteKey, out var raw) ? raw as string : null;

        // `uncategorized` là project ảo — không có hàng nào trong bảng cấp quyền, nên tra theo nó
        // sẽ luôn ra rỗng và **không ai** vào được, kể cả manager. Xử như trường hợp không có
        // project: mở được màn hình, còn ai thực sự thấy thì VirtualProject.CanSee quyết định.
        var virtualScope = string.IsNullOrEmpty(project)
            || Modules.Tickets.Organization.VirtualProject.IsUncategorized(project);

        IEnumerable<string> scoped = virtualScope
            ? snapshot.ProjectPermissions.Values.SelectMany(x => x)
            : snapshot.ProjectPermissions.TryGetValue(project!.ToLowerInvariant(), out var forProject)
                ? forProject
                : Array.Empty<string>();

        return snapshot.Permissions.Concat(scoped).Distinct();
    }

    /// <summary>
    /// Token được phát **trước hoặc cùng giây** với mốc <paramref name="changedAt"/> hay không.
    ///
    /// "Hoặc cùng giây" là cố ý: `iat` chỉ có độ phân giải giây, nên một phiên đăng nhập cùng
    /// giây với lúc đổi mật khẩu mang đúng con số ấy. So sánh chặt thì phiên đó lọt qua — đúng
    /// kịch bản mà việc đổi mật khẩu sinh ra để chặn. Token cấp cho chính lượt đổi được đẩy `iat`
    /// lên một giây nên không dính.
    ///
    /// Thiếu `iat` thì coi là **cũ** và từ chối: token không nói được nó phát lúc nào thì không
    /// có cách nào khẳng định nó phát sau lần đổi mật khẩu. Chọn phía an toàn — người dùng chỉ
    /// cần đăng nhập lại.
    /// </summary>
    private static bool IssuedBefore(ClaimsPrincipal user, DateTimeOffset changedAt)
    {
        var raw = user.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Iat)?.Value;
        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return true;
        }

        // Cắt mốc về giây trước khi so: `changedAt` lấy từ database có phần lẻ, còn `iat` thì
        // không — không cắt thì token phát đúng giây đó luôn nhỏ hơn và bị từ chối oan... hoặc
        // ngược lại, tuỳ phần lẻ. Cắt rồi so `<=` cho kết quả không phụ thuộc mili-giây.
        var floor = DateTimeOffset.FromUnixTimeSeconds(changedAt.ToUnixTimeSeconds());
        return DateTimeOffset.FromUnixTimeSeconds(seconds) <= floor;
    }

    /// <summary>
    /// Khoá nhớ tạm quyền của một user. Công khai để chỗ đổi mật khẩu xoá được đúng khoá này —
    /// hai nơi tự ghép chuỗi thì sớm muộn cũng lệch nhau và việc thu hồi lặng lẽ mất tác dụng.
    /// </summary>
    public static string CacheKey(Guid userId) => $"auth:{userId}";

    private static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        var raw = user.FindFirst(AppClaimTypes.Subject)?.Value
                  ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out userId);
    }

    private static async Task<UserAuthSnapshot?> LoadAsync(
        Guid userId, AppDbContext db, IMemoryCache cache, AuthOptions options, CancellationToken ct)
    {
        var ttl = options.PermissionCacheSeconds;

        if (ttl <= 0)
        {
            return await QueryAsync(userId, db, ct);
        }

        var key = CacheKey(userId);
        if (cache.TryGetValue<UserAuthSnapshot?>(key, out var cached))
        {
            return cached;
        }

        var fresh = await QueryAsync(userId, db, ct);
        cache.Set(key, fresh, TimeSpan.FromSeconds(ttl));
        return fresh;
    }

    /// <summary>
    /// Một truy vấn duy nhất cho cả vai trò lẫn permission. Hai index
    /// <c>ix_user_roles_role_id</c> và khóa chính kép của bảng nối gánh phần join này.
    /// </summary>
    private static async Task<UserAuthSnapshot?> QueryAsync(Guid userId, AppDbContext db, CancellationToken ct)
    {
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Email,
                u.Login,
                u.DisplayName,
                u.IsActive,
                u.PasswordChangedAt,
                Roles = u.UserRoles.Select(ur => ur.Role.Name).ToList(),
                // Chỉ vai trò toàn cục mới cấp quyền toàn cục. Thiếu điều kiện này thì mọi phép
                // kiểm theo project đều rơi vào nhánh dự phòng toàn cục và việc cách ly không
                // bao giờ có hiệu lực — nhìn thì như đã làm, chạy thì như chưa.
                Permissions = u.UserRoles
                    .Where(ur => ur.Role.IsGlobal)
                    .SelectMany(ur => ur.Role.RolePermissions.Select(rp => rp.Permission.Code))
                    .ToList()
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        // Quyền theo project đến từ hai nguồn, và phải hợp lại:
        //   1. project_members — người này được cấp vai trò gì ở project nào (nhân viên);
        //   2. project_role_access — vai trò toàn cục của người này với tới project nào (khách).
        var fromMembership = await db.ProjectMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .SelectMany(m => m.Role.RolePermissions.Select(rp => new
            {
                Slug = db.Projects.Where(p => p.Id == m.ProjectId).Select(p => p.Slug).First(),
                rp.Permission.Code
            }))
            .ToListAsync(ct);

        // Với vai trò tự phục vụ (<see cref="Permissions.SelfServiceRoles"/>) thì hàng role-access
        // mới chỉ **mở** project ra thành danh mục; người dùng còn phải tự nhận. Giao của hai vế,
        // nên tự nhận một project chưa mở vẫn không vào được — ô tick không cấp thêm quyền cho ai.
        var fromRoleAccess = await db.ProjectRoleAccess.AsNoTracking()
            .Where(a => db.UserRoles.Any(ur => ur.UserId == userId && ur.RoleId == a.RoleId))
            .Where(a => !Permissions.SelfServiceRoles.Contains(a.Role.Name)
                || db.ProjectSubscriptions.Any(sub => sub.UserId == userId && sub.ProjectId == a.ProjectId))
            .SelectMany(a => a.Role.RolePermissions.Select(rp => new
            {
                Slug = db.Projects.Where(p => p.Id == a.ProjectId).Select(p => p.Slug).First(),
                rp.Permission.Code
            }))
            .ToListAsync(ct);

        var projectPermissions = fromMembership.Concat(fromRoleAccess)
            .GroupBy(x => x.Slug)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Code).Distinct().OrderBy(x => x).ToList());

        return new UserAuthSnapshot(
            row.Email,
            row.Login,
            row.DisplayName,
            row.IsActive,
            row.PasswordChangedAt,
            row.Roles.Distinct().OrderBy(x => x).ToList(),
            row.Permissions.Distinct().OrderBy(x => x).ToList(),
            projectPermissions);
    }

    private sealed record UserAuthSnapshot(
        string Email,
        string Login,
        string DisplayName,
        bool IsActive,
        DateTimeOffset? PasswordChangedAt,
        IReadOnlyList<string> Roles,
        IReadOnlyList<string> Permissions,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectPermissions);
}
