using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Incidents;
using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Revisions;

// ---------------- DTO ----------------

/// <summary>
/// Một dòng lịch sử sửa đổi. Trả về đúng những gì bảng lưu, không diễn giải thêm: giao diện
/// tự quyết định bày dạng diff hay dạng hai cột.
/// </summary>
public sealed record RevisionResponse(
    Guid Id,
    string Field,
    string? OldValue,
    string? NewValue,
    UserRef EditedBy,
    DateTimeOffset EditedAt,
    /// <summary>Người sửa không phải chủ bản ghi — một lần kiểm duyệt, không phải tác giả tự sửa.</summary>
    bool OnBehalf,
    string? Reason);

/// <summary>
/// Chữ ký của lần sửa gần nhất, đính kèm mọi response có thể sửa được.
///
/// <c>null</c> ở phía response nghĩa là bản ghi còn nguyên bản — giao diện không phải đoán
/// bằng cách so <c>createdAt</c> với <c>updatedAt</c>.
/// </summary>
public sealed record EditSignature(UserRef By, DateTimeOffset At);

/// <summary>
/// Bản ghi có nội dung sửa được. Bốn thực thể cùng mang dấu vết "sửa lần cuối bởi ai, lúc
/// nào", nên chúng khai cùng một hợp đồng thay vì để <see cref="ContentRevisionService"/>
/// phải biết mặt từng lớp một.
/// </summary>
public interface IEditableContent
{
    DateTimeOffset? LastEditedAt { get; set; }
    Guid? LastEditedBy { get; set; }

    /// <summary>Giá trị phía sau <c>ETag</c>/<c>If-Match</c> — xem <see cref="Domain.Incident.Version"/>.</summary>
    int Version { get; set; }
}

// ---------------- Service ----------------

/// <summary>
/// Một cửa duy nhất cho mọi lần sửa nội dung: so sánh giá trị cũ–mới, ghi lịch sử, đóng dấu
/// chữ ký lên bản ghi cha.
///
/// Gom về một chỗ vì phần dễ quên nhất của tính năng này là phần ghi lại. Nếu mỗi service tự
/// viết vài dòng <c>_db.ContentRevisions.Add(...)</c> thì chỉ cần một nhánh <c>if</c> nào đó
/// quên gọi là có một đường sửa **không để lại dấu vết** — đúng thứ mà cả tính năng này sinh
/// ra để chặn.
///
/// Cố ý KHÔNG tự gọi <c>SaveChangesAsync</c>: việc đổi nội dung và việc ghi lịch sử phải nằm
/// trong cùng một transaction do người gọi làm chủ, hệt như cặp "đổi trạng thái + ghi lịch
/// sử" của <see cref="IncidentService"/>.
/// </summary>
public sealed class ContentRevisionService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public ContentRevisionService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Mở một lần sửa trên <paramref name="target"/>.
    ///
    /// <paramref name="ownerId"/> là chủ bản ghi (người báo cáo sự cố, tác giả bình luận…) —
    /// dùng để đánh dấu <see cref="ContentRevision.OnBehalf"/>. Truyền <c>null</c> khi bản ghi
    /// không có chủ rõ ràng; khi đó mọi lần sửa đều tính là nhân danh chính mình.
    /// </summary>
    public EditDraft Begin(
        EditableEntityType entityType, Guid entityId, IEditableContent target,
        Guid actorId, Guid? ownerId, string? reason = null)
        => new(_db, entityType, entityId, target, actorId,
            onBehalf: ownerId is not null && ownerId != actorId,
            reason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            now: _clock.GetUtcNow());

    /// <summary>
    /// Lịch sử sửa của một bản ghi, cũ nhất trước — đọc từ trên xuống là đọc được diễn biến.
    ///
    /// Không kiểm quyền ở đây: lịch sử là dữ liệu của bản ghi cha nên nó phải chịu **đúng**
    /// ràng buộc đọc của bản ghi cha, và chỉ người gọi mới biết ràng buộc đó. Mọi endpoint
    /// dùng hàm này đều kiểm quyền đọc bản ghi cha trước khi gọi.
    /// </summary>
    public async Task<IReadOnlyList<RevisionResponse>> ListAsync(
        EditableEntityType entityType, Guid entityId, CancellationToken ct)
    {
        var rows = await _db.ContentRevisions.AsNoTracking()
            .Include(r => r.EditedByUser)
            .Where(r => r.EntityType == entityType && r.EntityId == entityId)
            .OrderBy(r => r.EditedAt).ThenBy(r => r.Id)
            .ToListAsync(ct);

        return rows.Select(r => new RevisionResponse(
            r.Id, r.Field, r.OldValue, r.NewValue,
            new UserRef(r.EditedByUser.Id, r.EditedByUser.DisplayName, r.EditedByUser.Email),
            r.EditedAt, r.OnBehalf, r.Reason)).ToList();
    }

    /// <summary>Chữ ký sửa lần cuối, dựng từ hai cột đã đóng dấu sẵn trên bản ghi.</summary>
    public static EditSignature? SignatureOf(DateTimeOffset? at, User? by)
        => at is null || by is null
            ? null
            : new EditSignature(new UserRef(by.Id, by.DisplayName, by.Email), at.Value);
}

/// <summary>
/// Một lần sửa đang soạn. Gom từng trường bằng <see cref="Change"/>, rồi
/// <see cref="Record"/> để đưa cả lịch sử lẫn chữ ký vào change tracker.
/// </summary>
public sealed class EditDraft
{
    private readonly AppDbContext _db;
    private readonly EditableEntityType _entityType;
    private readonly Guid _entityId;
    private readonly IEditableContent _target;
    private readonly Guid _actorId;
    private readonly bool _onBehalf;
    private readonly string? _reason;
    private readonly DateTimeOffset _now;
    private readonly List<ContentRevision> _rows = new();

    internal EditDraft(AppDbContext db, EditableEntityType entityType, Guid entityId,
        IEditableContent target, Guid actorId, bool onBehalf, string? reason, DateTimeOffset now)
    {
        _db = db;
        _entityType = entityType;
        _entityId = entityId;
        _target = target;
        _actorId = actorId;
        _onBehalf = onBehalf;
        _reason = reason;
        _now = now;
    }

    public bool HasChanges => _rows.Count > 0;

    /// <summary>
    /// Ghi nhận một trường đổi giá trị. Giá trị mới trùng giá trị cũ thì bỏ qua, kể cả khi
    /// client có gửi trường đó lên: bấm "Lưu" mà không sửa gì không phải là một lần sửa, và
    /// một dòng lịch sử rỗng chỉ làm loãng những dòng có thật.
    /// </summary>
    public EditDraft Change(string field, string? oldValue, string? newValue)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return this;
        }

        _rows.Add(new ContentRevision
        {
            Id = Guid.NewGuid(),
            EntityType = _entityType,
            EntityId = _entityId,
            Field = field,
            OldValue = oldValue,
            NewValue = newValue,
            EditedBy = _actorId,
            EditedAt = _now,
            OnBehalf = _onBehalf,
            Reason = _reason
        });

        return this;
    }

    /// <summary>Như <see cref="Change"/> cho trường không phải chuỗi — enum lưu bằng tên.</summary>
    public EditDraft Change<T>(string field, T oldValue, T newValue) where T : struct, Enum
        => Change(field, oldValue.ToString(), newValue.ToString());

    /// <summary>
    /// Đưa lịch sử và chữ ký vào change tracker. Người gọi tự <c>SaveChangesAsync</c> — cùng
    /// một transaction với việc đổi nội dung, nếu không thì có đường để nội dung đổi mà lịch
    /// sử không kịp ghi.
    ///
    /// Không có thay đổi nào thì không đụng gì cả, và trả <c>false</c> để người gọi biết mà
    /// bỏ qua cả bước lưu lẫn dòng log.
    /// </summary>
    public bool Record()
    {
        if (!HasChanges)
        {
            return false;
        }

        _db.ContentRevisions.AddRange(_rows);
        _target.LastEditedAt = _now;
        _target.LastEditedBy = _actorId;

        // Một lần sửa = một lần tăng, dù lần đó đổi một trường hay cả ba: phiên bản nói "bản ghi
        // đã khác đi", không đếm số ô đã sửa.
        _target.Version++;
        return true;
    }

    /// <summary>Tên các trường đã đổi — dùng cho dòng audit log, nơi không được ghi nội dung.</summary>
    public string ChangedFields() => string.Join(",", _rows.Select(r => r.Field));
}
