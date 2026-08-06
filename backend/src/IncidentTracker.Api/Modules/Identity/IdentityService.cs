using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Observability;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IncidentTracker.Api.Modules.Identity;

public sealed class LoginRequest
{
    /// <summary>
    /// Email hoặc username. Đổi tên từ <c>email</c> vì trường này giờ nhận cả hai — giữ tên cũ
    /// thì mọi người đọc API sau này đều hiểu sai, và ràng buộc <c>[EmailAddress]</c> đi kèm sẽ
    /// chặn thẳng username.
    ///
    /// Nhận diện bằng dấu <c>@</c>: username theo quy tắc GitHub (<see cref="LoginNames"/>) chỉ
    /// gồm chữ thường, số và gạch nối nên không bao giờ chứa <c>@</c>.
    /// </summary>
    [Required, MaxLength(320)]
    public string Identifier { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Password { get; set; } = string.Empty;
}

public sealed record LoginResponse(
    string AccessToken,
    string TokenType,
    int ExpiresInSeconds,
    DateTimeOffset ExpiresAt,
    Guid UserId,
    string Email,
    string Login,
    string DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

public sealed class RegisterRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Tên đăng nhập do người dùng chọn. Trước đây sinh tự động từ phần trước <c>@</c> của email
    /// và khử trùng bằng hậu tố số, nên hai người tên giống nhau thì người sau lặng lẽ thành
    /// <c>kien-2</c> mà không ai báo. Giờ họ tự chọn, và trùng thì nhận 409 để chọn lại.
    /// </summary>
    [Required, MaxLength(LoginNames.MaxLength), MinLength(1)]
    public string Username { get; set; } = string.Empty;

    [Required, MaxLength(150), MinLength(1)]
    public string DisplayName { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Password { get; set; } = string.Empty;

    // Cố ý KHÔNG có trường roles hay isActive: đây là đường công khai, người đăng ký không
    // được quyết định mình có vai trò gì (chống leo thang đặc quyền).
}

public sealed class ChangePasswordRequest
{
    /// <summary>
    /// Bắt buộc. Không có nó thì ai mượn được máy đang mở là chiếm luôn tài khoản — token trong
    /// tay đã đủ để đổi mật khẩu và khoá chủ cũ ra ngoài.
    /// </summary>
    [Required, MaxLength(200)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string NewPassword { get; set; } = string.Empty;
}

public sealed record RegisterResponse(
    Guid UserId,
    string Email,
    string Login,
    string DisplayName,
    IReadOnlyList<string> Roles,
    bool RequiresApproval,
    /// <summary>Phiên đăng nhập cấp luôn khi tài khoản active; null khi còn chờ admin duyệt.</summary>
    LoginResponse? Session);

/// <summary>
/// CMP-02 Identity Service (FR-006..008) — xác thực credential và phát JWT có chữ ký.
/// </summary>
public sealed class IdentityService
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _hasher;
    private readonly JwtOptions _jwt;
    private readonly IOptionsMonitor<RegistrationOptions> _registration;
    private readonly AppMetrics _metrics;
    private readonly ILogger<IdentityService> _logger;
    private readonly TimeProvider _clock;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

    public IdentityService(AppDbContext db, IPasswordHasher<User> hasher,
        IOptions<JwtOptions> jwt, IOptionsMonitor<RegistrationOptions> registration,
        AppMetrics metrics, ILogger<IdentityService> logger, TimeProvider clock,
        Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt.Value;
        _registration = registration;
        _metrics = metrics;
        _logger = logger;
        _clock = clock;
        _cache = cache;
    }

    /// <summary>
    /// SEQ-01. Sai credential và user inactive trả về cùng một lỗi chung để không lộ
    /// email nào đang tồn tại (mục 6.5, BR-04).
    /// </summary>
    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        // Có `@` thì là email, không thì là username: username theo quy tắc GitHub không chứa `@`.
        var identifier = request.Identifier.Trim();
        var isEmail = identifier.Contains('@');
        var normalized = isEmail ? NormalizeEmail(identifier) : identifier.ToLowerInvariant();

        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => isEmail ? u.Email == normalized : u.Login == normalized, ct);

        if (user is null)
        {
            // Vẫn băm một lần để thời gian phản hồi không tiết lộ tài khoản có tồn tại hay không.
            // Nhánh username phải làm y hệt nhánh email, nếu không thì chỉ cần đo thời gian phản
            // hồi là dò được username nào có thật.
            _hasher.HashPassword(new User(), request.Password);
            _metrics.LoginFailed(isEmail ? "unknown_email" : "unknown_login");
            _logger.LogWarning("Đăng nhập thất bại: tài khoản không tồn tại.");
            return null;
        }

        var verification = _hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            _metrics.LoginFailed("bad_password");
            _logger.LogWarning("Đăng nhập thất bại cho user {UserId}: sai mật khẩu.", user.Id);
            return null;
        }

        // FR-008 / BR-04: user inactive không nhận token mới.
        if (!user.IsActive)
        {
            _metrics.LoginFailed("inactive_user");
            _logger.LogWarning("Đăng nhập thất bại cho user {UserId}: tài khoản đã bị vô hiệu hóa.", user.Id);
            return null;
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, request.Password);
            await _db.SaveChangesAsync(ct);
        }

        var roles = user.UserRoles.Select(ur => ur.Role.Name).Distinct().OrderBy(x => x).ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct().OrderBy(x => x).ToList();

        var (token, expiresAt) = CreateToken(user);

        _metrics.LoginSucceeded();
        _logger.LogInformation("Đăng nhập thành công cho user {UserId} với {PermissionCount} permission.",
            user.Id, permissions.Count);

        return new LoginResponse(
            token, "Bearer", _jwt.ExpiryMinutes * 60, expiresAt,
            user.Id, user.Email, user.Login, user.DisplayName, roles, permissions);
    }

    /// <summary>
    /// Đổi mật khẩu của chính mình.
    ///
    /// Ba thứ phải đi cùng nhau, thiếu cái nào cũng thành lỗ hổng:
    ///
    /// 1. <b>Bắt nhập mật khẩu hiện tại.</b> Xem <see cref="ChangePasswordRequest.CurrentPassword"/>.
    /// 2. <b>Giới hạn tần suất</b> ngang đăng nhập — nếu không đây thành chỗ dò mật khẩu hiện tại
    ///    mà không bị khoá, vì người gọi đã có token hợp lệ.
    /// 3. <b>Các phiên khác phải chết.</b> Đổi mật khẩu vì nghi bị lộ mà token cũ vẫn sống thêm
    ///    một giờ thì việc đổi gần như vô nghĩa.
    ///
    /// Trả token mới cho **chính phiên đang đổi**: nếu để phiên này chết theo thì lần nào đổi
    /// xong người dùng cũng bị đá ra và sẽ tưởng là lỗi.
    ///
    /// Trả <c>null</c> khi mật khẩu hiện tại sai hoặc tài khoản không còn hiệu lực.
    /// </summary>
    public async Task<LoginResponse?> ChangePasswordAsync(
        ClaimsPrincipal principal, ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || !user.IsActive)
        {
            return null;
        }

        if (_hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword)
            == PasswordVerificationResult.Failed)
        {
            _logger.LogWarning("Đổi mật khẩu thất bại cho user {UserId}: sai mật khẩu hiện tại.", user.Id);
            return null;
        }

        var minLength = _registration.CurrentValue.MinPasswordLength;
        if (request.NewPassword.Length < minLength)
        {
            throw AppException.BadRequest($"Mật khẩu phải dài tối thiểu {minLength} ký tự.");
        }

        user.PasswordHash = _hasher.HashPassword(user, request.NewPassword);

        // Mốc này là thứ giết các phiên khác: middleware từ chối mọi token có `iat` **nhỏ hơn
        // hoặc bằng** nó.
        //
        // Phải là "hoặc bằng", và token sống sót phải được đẩy lên một giây, vì `iat` chỉ có độ
        // phân giải giây: một phiên đăng nhập cùng giây với lúc đổi mật khẩu sẽ mang đúng `iat`
        // ấy. So sánh chặt (`<`) thì phiên đó lọt qua — mà đó đúng là kịch bản người ta đổi mật
        // khẩu vì nghi bị lộ, kẻ kia vừa đăng nhập xong.
        var now = _clock.GetUtcNow();
        user.PasswordChangedAt = now;
        await _db.SaveChangesAsync(ct);

        // Nếu quyền đang được nhớ tạm (Auth:PermissionCacheSeconds > 0) thì phải xoá ngay, nếu
        // không token cũ vẫn sống thêm đúng khoảng TTL. Với thu hồi quyền thường thì độ trễ đó
        // chấp nhận được; với đổi mật khẩu vì nghi bị lộ thì không.
        _cache.Remove(PrincipalEnrichmentMiddleware.CacheKey(user.Id));

        _logger.LogInformation("User {UserId} đã đổi mật khẩu; các phiên khác bị vô hiệu.", user.Id);

        var roles = user.UserRoles.Select(ur => ur.Role.Name).OrderBy(x => x).ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct().OrderBy(x => x).ToList();

        // `iat` đẩy lên một giây để token này nằm hẳn sau mốc; `nbf` vẫn là hiện tại nên nó dùng
        // được ngay. `iat` không tham gia việc kiểm chữ ký hay hạn dùng, chỉ phục vụ đúng phép so
        // sánh ở trên.
        var (token, expiresAt) = CreateToken(user, issuedAt: now.AddSeconds(1));
        return new LoginResponse(token, "Bearer", _jwt.ExpiryMinutes * 60, expiresAt,
            user.Id, user.Email, user.Login, user.DisplayName, roles, permissions);
    }

    /// <summary>
    /// UC-08 · Tự đăng ký tài khoản.
    ///
    /// Ba ràng buộc giữ cho đường công khai này không thành lỗ hổng:
    /// người đăng ký không chọn được vai trò, vai trò mặc định bị lọc qua danh sách trắng
    /// cứng trong mã, và tên miền email có thể giới hạn bằng cấu hình.
    /// </summary>
    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var options = _registration.CurrentValue;

        if (!options.Enabled)
        {
            throw AppException.Forbidden(
                "Hệ thống đang tắt tính năng tự đăng ký. Liên hệ quản trị viên để được cấp tài khoản.");
        }

        var email = NormalizeEmail(request.Email);

        if (!options.IsEmailDomainAllowed(email))
        {
            var allowed = string.Join(", ", options.AllowedDomainList());
            _metrics.RegistrationRejected("domain_not_allowed");
            throw AppException.BadRequest(
                $"Hệ thống chỉ nhận email thuộc tên miền: {allowed}.");
        }

        if (request.Password.Length < options.MinPasswordLength)
        {
            throw AppException.BadRequest(
                $"Mật khẩu phải dài tối thiểu {options.MinPasswordLength} ký tự.");
        }

        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
        {
            _metrics.RegistrationRejected("email_taken");
            throw AppException.Conflict($"Email '{email}' đã được đăng ký.");
        }

        var username = request.Username.Trim().ToLowerInvariant();
        if (!LoginNames.ValidLogin().IsMatch(username))
        {
            _metrics.RegistrationRejected("username_invalid");
            throw AppException.BadRequest(
                $"Username '{username}' không hợp lệ: chỉ chữ thường, số và gạch nối; "
                + $"không mở đầu hay kết thúc bằng gạch nối; tối đa {LoginNames.MaxLength} ký tự.");
        }

        if (await _db.Users.AnyAsync(u => u.Login == username, ct))
        {
            // 409 ở đây có lộ "username này đã có người dùng", nhưng đó là điều bắt buộc phải nói
            // để họ chọn tên khác. Khác hẳn `POST /login`, chỗ đó thì tuyệt đối không được lộ.
            _metrics.RegistrationRejected("username_taken");
            throw AppException.Conflict($"Username '{username}' đã có người dùng.");
        }

        var wantedRoles = options.SafeDefaultRoles();
        // Phải Include tới Permission: token cấp ngay sau khi đăng ký lấy claims từ đây, thiếu
        // Include thì người dùng đăng nhập được nhưng không có quyền nào.
        var roles = wantedRoles.Count == 0
            ? new List<Role>()
            : await _db.Roles
                .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
                .Where(r => wantedRoles.Contains(r.Name))
                .ToListAsync(ct);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Login = username,
            DisplayName = request.DisplayName.Trim(),
            // RequireApproval: tài khoản tồn tại nhưng chưa đăng nhập được cho tới khi admin bật.
            IsActive = !options.RequireApproval,
            CreatedAt = _clock.GetUtcNow()
        };
        user.PasswordHash = _hasher.HashPassword(user, request.Password);
        user.UserRoles = roles.Select(r => new UserRole { UserId = user.Id, RoleId = r.Id }).ToList();

        _db.Users.Add(user);
        // Unique index mới là thứ thực sự chặn trùng; kiểm tra ở trên chỉ để có thông báo đẹp và
        // để nói đúng trường nào bị trùng.
        await _db.SaveTranslatingConflictAsync($"Email '{email}' hoặc username '{username}' đã được dùng.", ct);

        var roleNames = roles.Select(r => r.Name).OrderBy(x => x).ToList();

        _metrics.RegistrationSucceeded(options.RequireApproval);
        _logger.LogInformation(
            "Audit identity.register actor={UserId} target={UserId} roles={Roles} pendingApproval={Pending} result=success",
            user.Id, user.Id, string.Join(',', roleNames), options.RequireApproval);

        // Chờ duyệt thì không cấp token — nếu cấp, tài khoản chưa được duyệt vẫn gọi được API.
        LoginResponse? session = null;
        if (user.IsActive)
        {
            var permissions = roles
                .SelectMany(r => r.RolePermissions)
                .Select(rp => rp.Permission.Code)
                .Distinct().OrderBy(x => x).ToList();

            var (token, expiresAt) = CreateToken(user);
            session = new LoginResponse(token, "Bearer", _jwt.ExpiryMinutes * 60, expiresAt,
                user.Id, user.Email, user.Login, user.DisplayName, roleNames, permissions);
        }

        return new RegisterResponse(
            user.Id, user.Email, user.Login, user.DisplayName, roleNames, options.RequireApproval, session);
    }

    /// <summary>
    /// FR-007 · ADR-004 — token chỉ mang những gì <b>không</b> tra ngược được từ user id:
    /// <c>sub</c>, <c>jti</c>, <c>iss</c>, <c>aud</c>, <c>exp</c>.
    ///
    /// Email, tên hiển thị, vai trò và permission đều suy ra được từ <c>sub</c> nên bị bỏ ra
    /// khỏi token: chúng làm token phình to trên mọi request, và vì payload JWT chỉ là base64
    /// nên chúng còn phơi ra danh mục năng lực của hệ thống cho bất kỳ ai cầm được token.
    /// Những giá trị ấy được nạp lại từ database ở mỗi request bởi
    /// <see cref="PrincipalEnrichmentMiddleware"/>.
    /// </summary>
    /// <param name="issuedAt">
    /// Ghi đè mốc phát ghi vào <c>iat</c>. Chỉ lượt đổi mật khẩu dùng tới — xem
    /// <see cref="ChangePasswordAsync"/> để hiểu vì sao phải đẩy lên một giây.
    /// </param>
    private (string Token, DateTimeOffset ExpiresAt) CreateToken(
        User user, DateTimeOffset? authTime = null, DateTimeOffset? issuedAt = null)
    {
        var now = _clock.GetUtcNow();
        var expiresAt = now.AddMinutes(_jwt.ExpiryMinutes);
        var issuedFirstAt = authTime ?? now;
        var stamp = issuedAt ?? now;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            // `iat` là mốc phát của **chính token này**, khác `auth_time` (mốc đăng nhập đầu tiên,
            // giữ nguyên qua mọi lần cấp lại). Thu hồi sau khi đổi mật khẩu phải so với `iat`:
            // so với `auth_time` thì token vừa phát cho phiên đang đổi cũng bị giết theo, vì nó
            // mang đúng `auth_time` cũ.
            new(JwtRegisteredClaimNames.Iat, stamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            // `auth_time` giữ mốc đăng nhập **đầu tiên** và được mang sang mọi lần cấp lại. Đây
            // là thứ duy nhất cho phép chặn trần phiên mà không cần bảng lưu phiên ở server.
            new(AuthTimeClaim, issuedFirstAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwt.Issuer,
            audience: _jwt.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    /// <summary>
    /// Cấp lại token cho phiên đang chạy. Frontend gọi khi token sắp hết hạn <b>và</b> người dùng
    /// còn thao tác, nên phiên trượt theo hoạt động thật chứ không theo đồng hồ.
    ///
    /// Không phải refresh token: người gọi phải đang cầm một access token còn hiệu lực. Nghĩa là
    /// nó không kéo dài được một phiên đã chết, chỉ nối tiếp một phiên đang sống.
    ///
    /// Trả <c>null</c> khi tài khoản đã bị vô hiệu hóa hoặc phiên chạm trần
    /// <see cref="JwtOptions.SessionMaxHours"/>; controller dịch thành 401.
    /// </summary>
    public async Task<LoginResponse?> RefreshAsync(ClaimsPrincipal caller, CancellationToken ct)
    {
        var userId = caller.GetUserId();

        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role).ThenInclude(r => r.RolePermissions)
            .ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null || !user.IsActive)
        {
            _logger.LogWarning("Từ chối cấp lại phiên cho user {UserId}: không tồn tại hoặc đã bị vô hiệu hóa.", userId);
            return null;
        }

        var authTime = ReadAuthTime(caller) ?? _clock.GetUtcNow();
        if (_clock.GetUtcNow() - authTime >= TimeSpan.FromHours(_jwt.SessionMaxHours))
        {
            _logger.LogInformation("Phiên của user {UserId} chạm trần {Hours} giờ, buộc đăng nhập lại.",
                userId, _jwt.SessionMaxHours);
            return null;
        }

        var roles = user.UserRoles.Select(ur => ur.Role.Name).Distinct().OrderBy(x => x).ToList();
        var permissions = user.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.Permission.Code)
            .Distinct().OrderBy(x => x).ToList();

        var (token, expiresAt) = CreateToken(user, authTime);

        return new LoginResponse(
            token, "Bearer", _jwt.ExpiryMinutes * 60, expiresAt,
            user.Id, user.Email, user.Login, user.DisplayName, roles, permissions);
    }

    /// <summary>Claim giữ mốc đăng nhập đầu tiên của phiên (tên theo OIDC).</summary>
    public const string AuthTimeClaim = "auth_time";

    private static DateTimeOffset? ReadAuthTime(ClaimsPrincipal caller)
    {
        var raw = caller.FindFirst(AuthTimeClaim)?.Value;
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>BR-01 — email chuẩn hóa lowercase và trim trước khi so khớp hoặc lưu.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
