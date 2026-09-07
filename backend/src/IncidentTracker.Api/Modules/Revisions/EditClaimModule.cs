using System.ComponentModel.DataAnnotations;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Incidents;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Revisions;

// ---------------- Cấu hình ----------------

public sealed class EditClaimOptions
{
    public const string SectionName = "EditClaims";

    /// <summary>
    /// Một chỗ giữ sống được bao lâu nếu không ai gia hạn.
    ///
    /// Ngắn quá thì người soạn một mô tả dài bị rơi mất chỗ giữa chừng; dài quá thì một tab đã
    /// tắt còn bắt người khác gửi <c>If-Match</c> thêm hàng phút. Hai phút với nhịp gia hạn mỗi
    /// 45 giây cho phép lỡ một nhịp mà chưa mất chỗ.
    /// </summary>
    [Range(30, 3600)]
    public int TtlSeconds { get; set; } = 120;
}

// ---------------- DTO ----------------

/// <summary>Một người đang mở form sửa cùng bản ghi.</summary>
public sealed record ActiveEditor(UserRef User, DateTimeOffset Since, DateTimeOffset ExpiresAt);

/// <summary>
/// Kết quả của một lần giữ chỗ.
///
/// <paramref name="Version"/> đi kèm là cố ý: client vừa mở form thì cũng vừa nhận được đúng
/// giá trị cần đặt vào <c>If-Match</c> lúc lưu, không phải đi lấy ở một lời gọi khác.
/// </summary>
public sealed record EditClaimResponse(
    DateTimeOffset ExpiresAt,
    int Version,
    /// <summary>Những người khác đang cùng mở form. Rỗng nghĩa là chỉ mình bạn đang sửa.</summary>
    IReadOnlyList<ActiveEditor> Others);

// ---------------- Service ----------------

/// <summary>
/// Ai đang chiếm dụng bản ghi nào để sửa — và hệ quả của việc đó lên điều kiện ghi.
///
/// <b>Quy tắc duy nhất, và là toàn bộ lý do lớp này tồn tại:</b> khi có người **khác** đang giữ
/// chỗ trên một bản ghi, lần <c>PATCH</c> kế tiếp lên bản ghi đó **bắt buộc** mang
/// <c>If-Match</c> đúng phiên bản — thiếu thì <c>428</c>, lệch thì <c>412</c>. Không ai giữ chỗ
/// thì <c>If-Match</c> vẫn được tôn trọng nếu client gửi, nhưng không bắt buộc.
///
/// <b>Vì sao có điều kiện chứ không bắt buộc luôn.</b> Bắt buộc mọi lúc thì mọi client — kể cả
/// một dòng <c>curl</c> sửa một lỗi chính tả — đều phải đi hai vòng gọi. Cái giá đó chỉ đáng
/// trả đúng lúc có thật sự tranh chấp, và chỗ giữ là thứ nói cho hệ thống biết lúc nào là lúc
/// đó. Ngoài lúc tranh chấp, hành vi giữ nguyên như module Ticket vẫn làm.
///
/// <b>Không phải khoá.</b> Chỗ giữ không chặn ai. Nó chỉ nâng điều kiện ghi lên thành "phải
/// chứng minh anh đang nhìn đúng bản mới nhất". Xem chú thích của <see cref="EditClaim"/>.
/// </summary>
public sealed class EditClaimService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly IOptionsMonitor<EditClaimOptions> _options;

    public EditClaimService(AppDbContext db, TimeProvider clock, IOptionsMonitor<EditClaimOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options;
    }

    /// <summary>
    /// Nhận hoặc gia hạn chỗ giữ. Gọi lại nhiều lần là bình thường — đó chính là nhịp gia hạn
    /// của client đang mở form.
    /// </summary>
    public async Task<EditClaimResponse> ClaimAsync(
        EditableEntityType entityType, Guid entityId, int version, Guid actorId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var expiresAt = now.AddSeconds(_options.CurrentValue.TtlSeconds);

        await PurgeExpiredAsync(entityType, entityId, now, ct);

        var existing = await _db.EditClaims.FirstOrDefaultAsync(
            c => c.EntityType == entityType && c.EntityId == entityId && c.UserId == actorId, ct);

        if (existing is null)
        {
            _db.EditClaims.Add(new EditClaim
            {
                EntityType = entityType,
                EntityId = entityId,
                UserId = actorId,
                ClaimedAt = now,
                ExpiresAt = expiresAt
            });
        }
        else
        {
            existing.ExpiresAt = expiresAt;
        }

        await _db.SaveChangesAsync(ct);

        return new EditClaimResponse(expiresAt, version, await OthersAsync(entityType, entityId, actorId, ct));
    }

    /// <summary>
    /// Trả lại chỗ giữ. Idempotent: nhả một chỗ vốn không có (đã hết hạn, hoặc chưa từng giữ)
    /// không phải là lỗi — người dùng đóng tab hai lần không phải chuyện đáng dựng lỗi.
    /// </summary>
    public async Task ReleaseAsync(
        EditableEntityType entityType, Guid entityId, Guid actorId, CancellationToken ct)
    {
        await _db.EditClaims
            .Where(c => c.EntityType == entityType && c.EntityId == entityId && c.UserId == actorId)
            .ExecuteDeleteAsync(ct);

        await PurgeExpiredAsync(entityType, entityId, _clock.GetUtcNow(), ct);
    }

    /// <summary>Những người **khác** đang giữ chỗ và chưa hết hạn.</summary>
    public async Task<IReadOnlyList<ActiveEditor>> OthersAsync(
        EditableEntityType entityType, Guid entityId, Guid actorId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        var rows = await _db.EditClaims.AsNoTracking()
            .Include(c => c.User)
            .Where(c => c.EntityType == entityType && c.EntityId == entityId
                        && c.UserId != actorId && c.ExpiresAt > now)
            .OrderBy(c => c.ClaimedAt)
            .ToListAsync(ct);

        return rows
            .Select(c => new ActiveEditor(
                new UserRef(c.User.Id, c.User.DisplayName, c.User.Email), c.ClaimedAt, c.ExpiresAt))
            .ToList();
    }

    /// <summary>
    /// Cửa duy nhất mà mọi đường sửa nội dung đi qua trước khi ghi.
    ///
    /// Gọi **bên trong** transaction đã khoá hàng, nếu không thì phiên bản đem ra so là một bản
    /// chụp đã cũ và phép kiểm trở thành trang trí.
    /// </summary>
    public async Task EnsureWritableAsync(
        HttpRequest request, EditableEntityType entityType, Guid entityId, int currentVersion,
        Guid actorId, string subject, CancellationToken ct)
    {
        var others = await OthersAsync(entityType, entityId, actorId, ct);

        if (others.Count == 0)
        {
            EntityTags.EnsureIfMatch(request, currentVersion, subject);
            return;
        }

        EntityTags.RequireIfMatch(request, currentVersion, subject,
            others.Select(o => o.User.DisplayName).ToList());
    }

    /// <summary>
    /// Dọn hàng hết hạn của đúng bản ghi đang đụng tới.
    ///
    /// Dọn nhân tiện chứ không dựng một job nền: hàng hết hạn đã **không còn hiệu lực** từ lúc
    /// hết hạn (mọi truy vấn đều lọc theo <c>expires_at</c>), nên việc dọn chỉ là chuyện giữ
    /// bảng khỏi phình. Một job nền cho việc đó là một bộ phận chuyển động thêm mà không đổi
    /// được hành vi nào.
    /// </summary>
    private Task PurgeExpiredAsync(
        EditableEntityType entityType, Guid entityId, DateTimeOffset now, CancellationToken ct)
        => _db.EditClaims
            .Where(c => c.EntityType == entityType && c.EntityId == entityId && c.ExpiresAt <= now)
            .ExecuteDeleteAsync(ct);
}
