using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Identity;

/// <summary>
/// Hồ sơ người dùng nhìn từ bên ngoài.
///
/// Email và số điện thoại là <c>null</c> khi chủ tài khoản chưa bật cho người khác xem. Trả
/// <c>null</c> chứ không phải chuỗi rỗng để phía gọi phân biệt được "không có" với "không cho xem"
/// mà không cần thêm cờ.
/// </summary>
public sealed record ProfileResponse(
    Guid Id,
    string Login,
    string DisplayName,
    string? AvatarUrl,
    /// <summary>online · snooze · offline. `invisible` không bao giờ lộ ra cho người khác.</summary>
    string Status,
    string? Biography,
    string? Email,
    string? Phone,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Roles,
    bool IsSelf,
    /// <summary>Chỉ có giá trị khi tự xem hồ sơ mình — người khác không cần biết ai đang bật gì.</summary>
    bool? EmailVisible,
    bool? PhoneVisible);

public sealed class UpdateProfileRequest
{
    [MaxLength(150), MinLength(1)]
    public string? DisplayName { get; set; }

    [MaxLength(1000)]
    public string? Biography { get; set; }

    [MaxLength(32)]
    public string? Phone { get; set; }

    [MaxLength(400)]
    public string? AvatarKey { get; set; }

    /// <summary>Trạng thái tự đặt: Online, Snooze, Invisible, hoặc Offline (không đặt gì).</summary>
    public PresenceStatus? PresenceStatus { get; set; }

    public bool? EmailVisible { get; set; }

    public bool? PhoneVisible { get; set; }
}

/// <summary>
/// Hồ sơ người dùng.
///
/// ## Vì sao không dùng <c>GET /api/users/{id}</c>
///
/// Endpoint đó đòi <c>user.read</c> — chỉ admin có — và trả cả email, vai trò lẫn trạng thái hoạt
/// động của **mọi** tài khoản. Yêu cầu ở đây là bấm vào tên bất kỳ ai cũng mở được hồ sơ, nên nới
/// <c>user.read</c> ra cho tất cả là đổi một màn hình lấy toàn bộ danh bạ nội bộ.
///
/// Endpoint này tra theo <c>login</c> chứ không theo id: đó là thứ hiện trên giao diện
/// (<c>@bao-long</c>), nên đường dẫn hồ sơ đọc được và gõ tay được.
/// </summary>
[ApiController]
[Route("api/profiles")]
[Produces("application/json")]
public sealed class ProfilesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly Modules.Tickets.Storage.IStorageProvider _storage;
    private readonly PresenceTracker _presence;

    public ProfilesController(AppDbContext db, Modules.Tickets.Storage.IStorageProvider storage,
        PresenceTracker presence)
    {
        _db = db;
        _storage = storage;
        _presence = presence;
    }

    /// <summary>Hồ sơ công khai của một người. Mọi tài khoản đã đăng nhập đều xem được.</summary>
    [HttpGet("{login}")]
    [Authorize]
    [ProducesResponseType(typeof(ProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfileResponse>> Get(string login, CancellationToken ct)
    {
        var normalized = login.Trim().TrimStart('@').ToLowerInvariant();
        var user = await _db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Login == normalized, ct)
            ?? throw AppException.NotFound($"Không tìm thấy người dùng '{login}'.");

        return Ok(await ToResponseAsync(user, User.GetUserId(), ct));
    }

    /// <summary>Sửa hồ sơ của **chính mình**. Không có đường sửa hồ sơ người khác ở đây.</summary>
    [HttpPatch("me")]
    [Authorize]
    [ProducesResponseType(typeof(ProfileResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProfileResponse>> UpdateMe(UpdateProfileRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await _db.Users
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw AppException.NotFound("Không tìm thấy tài khoản.");

        if (request.DisplayName is { } name)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) throw AppException.BadRequest("Tên hiển thị không được để trống.");
            user.DisplayName = trimmed;
        }

        // Chuỗi rỗng nghĩa là xoá trường đó, khác với không gửi (giữ nguyên) — nên `null` sau khi
        // trim chứ không lưu chuỗi rỗng, để mọi chỗ đọc chỉ phải kiểm một dạng "không có".
        if (request.Biography is { } bio) user.Biography = Blank(bio);
        if (request.Phone is { } phone) user.Phone = Blank(phone);
        if (request.AvatarKey is { } key) user.AvatarKey = Blank(key);
        if (request.PresenceStatus is { } presence) user.PresenceStatus = presence;
        if (request.EmailVisible is { } ev) user.EmailVisible = ev;
        if (request.PhoneVisible is { } pv) user.PhoneVisible = pv;

        await _db.SaveChangesAsync(ct);
        return Ok(await ToResponseAsync(user, userId, ct));
    }

    private static string? Blank(string value) => value.Trim() is { Length: > 0 } v ? v : null;

    private async Task<ProfileResponse> ToResponseAsync(Domain.User user, Guid callerId, CancellationToken ct)
    {
        var isSelf = user.Id == callerId;
        return new ProfileResponse(
            user.Id,
            user.Login,
            user.DisplayName,
            user.AvatarKey is null ? null : await _storage.PublicUrlAsync(user.AvatarKey, ct),
            Presence.Resolve(user.PresenceStatus, _presence.IsConnected(user.Id), isSelf),
            user.Biography,
            // Chủ tài khoản luôn thấy của mình; người khác chỉ thấy khi được bật.
            isSelf || user.EmailVisible ? user.Email : null,
            isSelf || user.PhoneVisible ? user.Phone : null,
            user.CreatedAt,
            user.UserRoles.Select(ur => ur.Role.Name).OrderBy(x => x).ToList(),
            isSelf,
            isSelf ? user.EmailVisible : null,
            isSelf ? user.PhoneVisible : null);
    }
}
