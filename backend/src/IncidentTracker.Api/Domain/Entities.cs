namespace IncidentTracker.Api.Domain;

/// <summary>ENT-User — source of truth cho identity (mục 5.5).</summary>
public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string DisplayName { get; set; } = null!;

    /// <summary>
    /// Tên đăng nhập ngắn dùng cho <c>@mention</c> (Architecture v3.1, mục 5.1). Unique, chữ
    /// thường, sinh từ phần trước <c>@</c> của email và khử trùng bằng hậu tố số.
    /// </summary>
    public string Login { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    // ---- Hồ sơ ----

    /// <summary>Khoá tệp ảnh đại diện trong kho lưu trữ. Null = dùng chữ viết tắt như trước.</summary>
    public string? AvatarKey { get; set; }

    public string? Phone { get; set; }

    public string? Biography { get; set; }

    /// <summary>
    /// Cho người khác thấy email trên trang hồ sơ. **Mặc định tắt**: người dùng cũ chưa hề đồng ý
    /// công khai, nên bật sẵn rồi chờ họ tự tắt là làm ngược.
    ///
    /// Công tắc chỉ chi phối **trang hồ sơ**. Email khách hàng vẫn hiện trong bảng Feedback cho
    /// nhân viên vì đó là dữ liệu nghiệp vụ, không phải trường hồ sơ.
    /// </summary>
    public bool EmailVisible { get; set; }

    /// <summary>Như <see cref="EmailVisible"/>, cho số điện thoại. Mặc định tắt.</summary>
    public bool PhoneVisible { get; set; }

    /// <summary>
    /// Mốc đổi mật khẩu gần nhất. Token phát **trước** mốc này bị từ chối, nên đổi mật khẩu là
    /// đá mọi phiên khác ra ngay lập tức.
    ///
    /// Không dùng <c>jti</c> để thu hồi: <c>jti</c> có sinh nhưng không ai kiểm, và dựng danh
    /// sách thu hồi cần thêm một kho lưu trữ. Một cột mốc thời gian làm đúng việc cần làm, và
    /// middleware vốn đã đọc bảng users mỗi request nên không tốn thêm truy vấn nào.
    /// </summary>
    public DateTimeOffset? PasswordChangedAt { get; set; }

    /// <summary>
    /// Trạng thái người dùng **tự đặt**. Trạng thái hiển thị còn phụ thuộc có kết nối hay không —
    /// xem <c>Presence.Resolve</c>.
    ///
    /// Mặc định <c>Offline</c> nghĩa là "không tự đặt gì", và khi có kết nối thì hiện thành
    /// online. `Invisible` là lựa chọn có ý thức nên phải lưu, không suy ra được.
    /// </summary>
    public Modules.Identity.PresenceStatus PresenceStatus { get; set; }

    public List<UserRole> UserRoles { get; set; } = new();
}

/// <summary>ENT-Role — tập permission có tên.</summary>
public class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>
    /// Vai trò này có hiệu lực **trên toàn hệ thống** hay chỉ trong những project được cấp.
    ///
    /// Chỉ <c>admin</c> là toàn cục. Mọi vai trò khác — kể cả vai trò tự tạo — chỉ có quyền ở
    /// project mà người mang nó được cấp, qua <see cref="ProjectMember"/> hoặc
    /// <see cref="ProjectRoleAccess"/>.
    ///
    /// **Mặc định false là cố ý.** Một vai trò mới lỡ tay tạo ra sẽ không có quyền ở đâu cả —
    /// phiền nhưng vô hại. Mặc định ngược lại thì nó có quyền ở mọi nơi, và không ai nhận ra cho
    /// tới khi muộn.
    /// </summary>
    public bool IsGlobal { get; set; }

    /// <summary>
    /// Cấp của vai trò — càng lớn càng cao (BR-SEC-08). Cấp của một user là rank cao nhất
    /// trong các role họ mang. Người dùng chỉ tác động được lên user có cấp THẤP HƠN mình và
    /// chỉ cấp được role có rank thấp hơn cấp của mình; thiếu ràng buộc này thì bất kỳ ai giữ
    /// <c>user.role.assign</c> đều tự nâng mình lên admin bằng đúng một request.
    /// </summary>
    public int Rank { get; set; }

    public List<UserRole> UserRoles { get; set; } = new();
    public List<RolePermission> RolePermissions { get; set; } = new();
}

/// <summary>ENT-Permission — năng lực nguyên tử dạng resource.action (BR-02).</summary>
public class Permission
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string? Description { get; set; }

    public List<RolePermission> RolePermissions { get; set; } = new();
}

/// <summary>ENT-UserRole — composite PK (user_id, role_id) chống trùng cặp (BR-03).</summary>
public class UserRole
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

/// <summary>
/// <b>Người này</b> có vai trò này <b>trên project này</b>.
///
/// Dùng cho nhân viên: support, responder, manager. Một người có thể có nhiều hàng, tức làm ở
/// nhiều project với vai trò khác nhau ở mỗi nơi.
///
/// Khác <see cref="UserRole"/> ở chỗ <see cref="UserRole"/> giờ mang nghĩa **vai trò toàn cục** —
/// admin, và để biết ai thuộc nhóm nào cho <see cref="ProjectRoleAccess"/> tra.
/// </summary>
public class ProjectMember
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// <b>Mọi người mang vai trò này</b> với tới project này.
///
/// Dùng cho khách hàng: cấp một lần cho cả vai trò thay vì cấp cho từng người — với hàng nghìn
/// khách hàng thì cấp từng người là việc không ai làm nổi.
///
/// Hai bảng chứ không một bảng có cột <c>UserId</c> cho phép null: hai câu hỏi khác nhau ("ai làm
/// ở project này" và "vai trò nào với tới project này") thì hai bảng, mỗi bảng trả lời đúng một
/// câu. Gộp lại thì mọi truy vấn đều phải kèm điều kiện null và người đọc phải tự đoán ý.
/// </summary>
/// <summary>
/// Khách hàng tự nhận project mình có dùng dịch vụ.
///
/// <see cref="ProjectRoleAccess"/> trả lời "project này có <b>mở</b> cho vai trò đó không" — quyết
/// định của quản trị. Bảng này trả lời "người này có <b>dùng</b> project đó không" — quyết định của
/// chính họ. Hai câu hỏi khác nhau nên hai bảng.
///
/// Quyền thật là <b>giao</b> của hai vế, nên tự nhận một project chưa mở thì vẫn không vào được:
/// ô tick không phải một đường tự cấp quyền.
/// </summary>
public class ProjectSubscription
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset JoinedAt { get; set; }
}

public class ProjectRoleAccess
{
    public Guid ProjectId { get; set; }
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>ENT-RolePermission — composite PK (role_id, permission_id) (BR-03).</summary>
public class RolePermission
{
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
    public Guid PermissionId { get; set; }
    public Permission Permission { get; set; } = null!;
}

/// <summary>Ba trạng thái vòng đời bắt buộc của Incident (Hình 3).</summary>
public enum IncidentStatus
{
    Investigating = 0,
    Mitigating = 1,
    Resolved = 2
}

public enum IncidentSeverity
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public enum FeedbackChannel
{
    Email = 0,
    Hotline = 1,
    Web = 2,
    Other = 3
}

/// <summary>
/// Vòng đời tiếp nhận của một Feedback (BR-BIZ-12). Chỉ tiến, không lùi:
/// New → Acknowledged (khi được gắn vào sự cố) → Responded (khi có người trả lời).
/// Phản hồi tự động của hệ thống không đổi trạng thái — nó chỉ xác nhận đã nhận được,
/// chưa phải là câu trả lời của con người.
/// </summary>
public enum FeedbackStatus
{
    New = 0,
    Acknowledged = 1,
    Responded = 2
}

/// <summary>ENT-Incident — aggregate root của module Incident Management.</summary>
public class Incident
{
    public Guid Id { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public IncidentSeverity Severity { get; set; } = IncidentSeverity.Medium;
    public IncidentStatus Status { get; set; } = IncidentStatus.Investigating;

    /// <summary>
    /// Project mà sự cố này thuộc về. <c>null</c> nghĩa là **chưa phân loại** — nó hiện dưới một
    /// project ảo tên <c>uncategorized</c> chỉ tồn tại trong mã, không có hàng nào trong bảng
    /// <c>projects</c>.
    ///
    /// Cho phép null chứ không ép chọn: dữ liệu cũ không có tín hiệu nào để đoán project, và gán
    /// bừa thì sau này không còn cách phân biệt "đã phân đúng" với "bị gán đại".
    /// </summary>
    public Guid? ProjectId { get; set; }

    public Guid ReporterId { get; set; }
    public User Reporter { get; set; } = null!;
    public Guid? AssigneeId { get; set; }
    public User? Assignee { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? MitigatingAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedBy { get; set; }
    public User? Resolver { get; set; }
    public bool IsDeleted { get; set; }

    public List<IncidentStatusHistory> StatusHistory { get; set; } = new();
    public List<Feedback> Feedbacks { get; set; } = new();
    public List<IncidentComment> Comments { get; set; } = new();
}

/// <summary>
/// ENT-IncidentComment — kênh trao đổi hai chiều trên một sự cố (UC-BIZ-09). Khác với
/// <see cref="IncidentStatusHistory"/> vốn là bằng chứng vòng đời, comment là hội thoại:
/// khách hàng bổ sung thông tin, kỹ thuật viên hỏi lại, và lời giải thích khi đóng sự cố.
/// </summary>
public class IncidentComment
{
    public Guid Id { get; set; }
    public Guid IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    public Guid AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public string Body { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>ENT-IncidentStatusHistory — bản ghi bất biến, chỉ append (BR-BIZ-06).</summary>
public class IncidentStatusHistory
{
    public Guid Id { get; set; }
    public Guid IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    public IncidentStatus FromStatus { get; set; }
    public IncidentStatus ToStatus { get; set; }
    public Guid ChangedBy { get; set; }
    public User ChangedByUser { get; set; } = null!;
    public DateTimeOffset ChangedAt { get; set; }
    public string? Note { get; set; }
}

/// <summary>ENT-Feedback — phản hồi khách hàng do Support ghi nhận.</summary>
public class Feedback
{
    public Guid Id { get; set; }
    public FeedbackChannel Channel { get; set; }
    public string? CustomerEmail { get; set; }
    public string Content { get; set; } = null!;

    /// <summary>Như <see cref="Incident.ProjectId"/>. <c>null</c> = chưa phân loại.</summary>
    public Guid? ProjectId { get; set; }

    public FeedbackStatus Status { get; set; } = FeedbackStatus.New;
    public Guid? IncidentId { get; set; }
    public Incident? Incident { get; set; }
    public Guid CreatedBy { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }

    public List<FeedbackReply> Replies { get; set; } = new();
}

/// <summary>
/// ENT-FeedbackReply — câu trả lời của doanh nghiệp (hoặc lời xác nhận tự động của hệ thống)
/// trên một Feedback. Đây là nửa còn thiếu của vòng khép kín: khách hàng gửi, doanh nghiệp
/// trả lời, khách hàng đọc được câu trả lời ngay trên phản hồi của mình.
/// </summary>
public class FeedbackReply
{
    public Guid Id { get; set; }
    public Guid FeedbackId { get; set; }
    public Feedback Feedback { get; set; } = null!;

    /// <summary>null khi và chỉ khi <see cref="IsAutomatic"/> — lời xác nhận do hệ thống sinh.</summary>
    public Guid? ResponderId { get; set; }
    public User? Responder { get; set; }

    public string Body { get; set; } = null!;
    public bool IsAutomatic { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
