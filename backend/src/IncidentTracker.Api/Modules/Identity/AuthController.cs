using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Identity;

/// <summary>UC-06 · API-Login.</summary>
[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public sealed class AuthController : ControllerBase
{
    private readonly IdentityService _identity;

    public AuthController(IdentityService identity) => _identity = identity;

    /// <summary>Xác thực email/password và phát JWT ngắn hạn (FR-006).</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await _identity.LoginAsync(request, ct);

        // Lỗi chung cho cả sai credential lẫn tài khoản inactive (SEQ-01).
        return result is null
            ? Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Đăng nhập thất bại",
                detail: "Email hoặc mật khẩu không đúng, hoặc tài khoản đã bị vô hiệu hóa.")
            : Ok(result);
    }

    /// <summary>
    /// Cấp lại token cho phiên đang chạy — phiên trượt theo hoạt động thật của người dùng.
    ///
    /// Frontend gọi khi token sắp hết hạn <b>và</b> người dùng còn thao tác kể từ lần cấp trước.
    /// Người gọi phải đang cầm token còn hiệu lực (<c>[Authorize]</c>), nên đây không phải
    /// refresh token và không hồi sinh được một phiên đã chết.
    ///
    /// 401 khi tài khoản bị vô hiệu hóa hoặc phiên chạm trần tuyệt đối; frontend đưa về đăng nhập.
    /// </summary>
    /// <summary>
    /// Đổi mật khẩu của chính mình. Giới hạn tần suất ngang đăng nhập vì đây cũng là chỗ đoán
    /// mật khẩu — chỉ khác là người đoán đã có token.
    /// </summary>
    [HttpPost("password")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var result = await _identity.ChangePasswordAsync(User, request, ct);

        return result is null
            ? Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Đổi mật khẩu thất bại",
                detail: "Mật khẩu hiện tại không đúng, hoặc tài khoản đã bị vô hiệu hóa.")
            : Ok(result);
    }

    [HttpPost("refresh")]
    [Authorize]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<LoginResponse>> Refresh(CancellationToken ct)
    {
        var result = await _identity.RefreshAsync(User, ct);

        return result is null
            ? Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Phiên đã kết thúc",
                detail: "Phiên làm việc đã hết hạn hoặc tài khoản không còn hoạt động. Vui lòng đăng nhập lại.")
            : Ok(result);
    }

    /// <summary>
    /// UC-08 · Tự đăng ký tài khoản.
    ///
    /// Đường công khai, nên có ba lớp chặn: rate limit theo IP, tên miền email cấu hình được,
    /// và vai trò được cấp bị lọc qua danh sách trắng cứng trong mã. Người đăng ký không gửi
    /// được trường <c>roles</c> hay <c>isActive</c> — DTO không có chúng.
    /// </summary>
    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Register)]
    [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<RegisterResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var result = await _identity.RegisterAsync(request, ct);
        return CreatedAtAction(nameof(Me), null, result);
    }

    /// <summary>
    /// Cho giao diện biết có nên hiện nút "Đăng ký" hay không, và ràng buộc nào đang áp dụng.
    /// Công khai vì trang đăng nhập cần đọc nó trước khi người dùng có token.
    /// </summary>
    [HttpGet("registration-policy")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(RegistrationPolicyResponse), StatusCodes.Status200OK)]
    public ActionResult<RegistrationPolicyResponse> RegistrationPolicy(
        [FromServices] IOptionsMonitor<RegistrationOptions> options)
    {
        var current = options.CurrentValue;
        return Ok(new RegistrationPolicyResponse(
            current.Enabled,
            current.AllowedDomainList(),
            current.RequireApproval,
            current.MinPasswordLength,
            current.SafeDefaultRoles()));
    }

    /// <summary>
    /// Trả về danh tính và quyền hiện hành đọc từ chính token đang dùng.
    /// Frontend dùng để dựng menu; quyết định quyền cuối cùng vẫn nằm ở API (ISS-07).
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(CurrentUserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<CurrentUserResponse> Me()
    {
        var permissions = User.Claims
            .Where(c => c.Type == AppClaimTypes.Permission)
            .Select(c => c.Value).OrderBy(x => x).ToList();
        var roles = User.Claims
            .Where(c => c.Type == System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value).OrderBy(x => x).ToList();

        return Ok(new CurrentUserResponse(
            User.GetUserId(),
            User.FindFirst(AppClaimTypes.Email)?.Value ?? string.Empty,
            User.FindFirst(AppClaimTypes.Login)?.Value ?? string.Empty,
            User.FindFirst(AppClaimTypes.DisplayName)?.Value ?? string.Empty,
            roles,
            permissions));
    }
}

public sealed record RegistrationPolicyResponse(
    bool Enabled,
    IReadOnlyList<string> AllowedEmailDomains,
    bool RequiresApproval,
    int MinPasswordLength,
    IReadOnlyList<string> DefaultRoles);

public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string Login,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
