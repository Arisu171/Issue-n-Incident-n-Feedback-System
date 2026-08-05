using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Identity;

/// <summary>
/// Trạng thái hiện diện, theo cách Discord làm.
///
/// <list type="bullet">
/// <item><c>online</c> — có ít nhất một kết nối đang mở.</item>
/// <item><c>snooze</c> — người dùng tự đặt "đừng làm phiền".</item>
/// <item><c>offline</c> — không kết nối nào.</item>
/// <item><c>invisible</c> — có kết nối, nhưng **người khác thấy là offline**.</item>
/// </list>
/// </summary>
public enum PresenceStatus
{
    Offline = 0,
    Online = 1,
    Snooze = 2,
    Invisible = 3,
}

public sealed class SetPresenceRequest
{
    [Required]
    public PresenceStatus Status { get; set; }
}

/// <summary>
/// Ai đang mở kết nối.
///
/// Đếm theo <b>connection</b> chứ không theo user: một người mở ba tab là ba kết nối, đóng một tab
/// không được làm họ thành offline. Đóng tab cuối mới hạ về 0.
///
/// Trong bộ nhớ tiến trình — cố ý, và đây là giới hạn phải nói rõ: chạy nhiều instance thì mỗi
/// instance chỉ biết kết nối của chính nó. Bản vẽ dùng Redis cho việc này; ở quy mô hiện tại thì
/// một bảng băm là đủ, và đổi sang Redis sau chỉ phải thay đúng lớp này.
/// </summary>
public sealed class PresenceTracker
{
    private readonly ConcurrentDictionary<Guid, int> _connections = new();

    public void Connected(Guid userId) => _connections.AddOrUpdate(userId, 1, (_, n) => n + 1);

    public void Disconnected(Guid userId)
    {
        _connections.AddOrUpdate(userId, 0, (_, n) => n - 1);
        // Dọn khoá về 0 để bảng không phình theo số tài khoản đã từng đăng nhập.
        if (_connections.TryGetValue(userId, out var left) && left <= 0)
        {
            _connections.TryRemove(userId, out _);
        }
    }

    public bool IsConnected(Guid userId) => _connections.TryGetValue(userId, out var n) && n > 0;
}

/// <summary>
/// Trạng thái nhìn từ bên ngoài.
///
/// <b>Điểm phải làm cho đúng: <c>invisible</c> được chặn ở server.</b> Nếu server vẫn gửi trạng
/// thái thật rồi để giao diện tự giấu thì mở tab Network là thấy — và một tính năng riêng tư hở
/// như vậy còn tệ hơn không có, vì người dùng tưởng mình đang ẩn.
/// </summary>
public static class Presence
{
    public static string Resolve(PresenceStatus stored, bool connected, bool isSelf)
    {
        // Chủ tài khoản luôn thấy trạng thái thật của mình, kể cả `invisible`.
        if (isSelf) return Name(stored == PresenceStatus.Offline && connected ? PresenceStatus.Online : stored);

        return stored switch
        {
            // Người khác thấy đúng như offline. Không phải "offline nhưng có cờ ẩn" — trả thẳng
            // offline, không kèm dấu hiệu nào để suy ra.
            PresenceStatus.Invisible => Name(PresenceStatus.Offline),
            PresenceStatus.Snooze when connected => Name(PresenceStatus.Snooze),
            _ => Name(connected ? PresenceStatus.Online : PresenceStatus.Offline),
        };
    }

    private static string Name(PresenceStatus s) => s.ToString().ToLowerInvariant();
}

[ApiController]
[Route("api/presence")]
[Produces("application/json")]
public sealed class PresenceController : ControllerBase
{
    private readonly AppDbContext _db;

    public PresenceController(AppDbContext db) => _db = db;

    /// <summary>Đặt trạng thái của chính mình. Không có đường đặt hộ người khác.</summary>
    [HttpPut("me")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SetMine(SetPresenceRequest request, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw AppException.NotFound("Không tìm thấy tài khoản.");

        user.PresenceStatus = request.Status;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
