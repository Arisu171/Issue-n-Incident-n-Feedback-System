using System.ComponentModel.DataAnnotations;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Revisions;

namespace IncidentTracker.Api.Modules.Incidents;

public sealed class CreateIncidentRequest
{
    [Required, MinLength(5), MaxLength(255)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(10_000)]
    public string? Description { get; set; }

    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Medium;

    // Cố ý KHÔNG có Status và ReporterId: BR-BIZ-01 và BR-BIZ-04 cấm client quyết định
    // trạng thái khởi tạo lẫn người ghi nhận. Field lạ trong body bị bỏ qua khi bind.
}

/// <summary>
/// PATCH nội dung sự cố — mọi trường tùy chọn, <c>null</c> = không đổi (cùng ngữ nghĩa với
/// <c>PATCH /tickets/{n}</c>).
///
/// Cố ý KHÔNG có <c>Status</c>: vòng đời đi qua <c>PATCH /status</c> với state machine một
/// chiều và lịch sử riêng của nó. Mở một đường thứ hai để đổi trạng thái là phá đúng thứ
/// FR-BIZ-05 dựng lên.
/// </summary>
public sealed class UpdateIncidentRequest
{
    [MinLength(5), MaxLength(255)]
    public string? Title { get; set; }

    /// <summary>Chuỗi rỗng = xóa mô tả; bỏ trống trường = giữ nguyên.</summary>
    [MaxLength(10_000)]
    public string? Description { get; set; }

    public IncidentSeverity? Severity { get; set; }

    /// <summary>
    /// Lý do sửa, đi vào mọi dòng lịch sử của lần sửa này. Tùy chọn với người tự sửa bài mình,
    /// nhưng là thứ nên điền khi sửa bài người khác — dòng lịch sử trả lời được "vì sao" thì
    /// mới thay được cho một cuộc tranh cãi sau này.
    /// </summary>
    [MaxLength(500)]
    public string? Reason { get; set; }
}

public sealed class UpdateStatusRequest : IStatusTargetRequest
{
    [Required]
    public IncidentStatus TargetStatus { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }
}

public sealed record IncidentResponse(
    Guid Id,
    string Title,
    string? Description,
    IncidentSeverity Severity,
    IncidentStatus Status,
    IncidentStatus? AllowedNextStatus,
    UserRef Reporter,
    UserRef? Assignee,
    DateTimeOffset CreatedAt,
    DateTimeOffset? MitigatingAt,
    DateTimeOffset? ResolvedAt,
    UserRef? Resolver,
    bool IsDeleted,
    /// <summary>null khi sự cố đã đóng — lúc đó đồng hồ SLA đã dừng.</summary>
    SlaStatus? Sla,
    /// <summary>null khi nội dung còn nguyên bản. Lịch sử đầy đủ ở <c>GET {id}/revisions</c>.</summary>
    EditSignature? LastEdit,
    /// <summary>Phiên bản nội dung — đặt vào <c>If-Match: "v{version}"</c> khi lưu.</summary>
    int Version);

public sealed record UserRef(Guid Id, string DisplayName, string Email);

/// <summary>
/// Chuyển một sự cố sang project khác — đây là thao tác **phân loại** cho mục
/// <c>uncategorized</c>, và cũng dùng được để sửa khi gán nhầm.
/// </summary>
public sealed class TransferProjectRequest
{
    /// <summary>Slug đích. <c>uncategorized</c> để trả về mục chưa phân loại.</summary>
    [System.ComponentModel.DataAnnotations.Required]
    public string ToProject { get; set; } = string.Empty;
}

/// <summary>
/// Một người có thể nhận sự cố. Cố ý **không có email**: đây là danh sách gợi ý cho ô chọn, không
/// phải bản ghi tài khoản, nên chỉ trả đúng thứ cần để hiển thị và để gửi lại.
/// </summary>
public sealed record AssignableUser(Guid Id, string Login, string DisplayName);

public sealed record StatusHistoryResponse(
    Guid Id,
    IncidentStatus FromStatus,
    IncidentStatus ToStatus,
    UserRef ChangedBy,
    DateTimeOffset ChangedAt,
    string? Note);

/// <summary>
/// FR-BIZ-08 · API-Incident-List. <c>createdFrom</c>/<c>createdTo</c> là phần bổ sung để
/// FR-BIZ-08 ("lọc theo khoảng thời gian tạo") có tham số thực thi — mục 6.6 không liệt kê.
/// </summary>
public sealed class IncidentListQuery : PagingQuery
{
    /// <summary>
    /// Tìm chữ trong tiêu đề và mô tả.
    ///
    /// Lọc **trong câu truy vấn** chứ không phải sau khi lấy về: lọc sau thì con số tổng của
    /// phân trang đếm cả những bản ghi không khớp, và trang 2 sẽ bỏ sót.
    /// </summary>
    [System.ComponentModel.DataAnnotations.MaxLength(200)]
    public string? Q { get; set; }

    public IncidentStatus? Status { get; set; }

    public Guid? AssigneeId { get; set; }

    public IncidentSeverity? Severity { get; set; }

    public DateTimeOffset? CreatedFrom { get; set; }

    public DateTimeOffset? CreatedTo { get; set; }

    /// <summary>Chỉ lấy sự cố đang vượt ngưỡng SLA (mục 6.9).</summary>
    public bool? SlaBreachedOnly { get; set; }

    /// <summary>Sắp xếp mặc định <c>created_at desc</c> theo mục 6.6.</summary>
    public bool OldestFirst { get; set; }
}
