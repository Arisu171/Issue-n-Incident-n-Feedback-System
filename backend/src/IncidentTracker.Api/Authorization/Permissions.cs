namespace IncidentTracker.Api.Authorization;

/// <summary>
/// Danh mục permission code — nguồn duy nhất, đối chiếu mục 9.2 của Architecture.md.
/// Bốn code role.*/permission.* là phần bổ sung so với 9.2 để FR-002 (CRUD role/permission)
/// thực thi được qua RBAC thay vì hard-code (xem README mục "Sai lệch có chủ đích").
/// </summary>
public static class Permissions
{
    public const string UserCreate = "user.create";
    public const string UserRead = "user.read";
    public const string UserUpdate = "user.update";
    public const string UserDelete = "user.delete";
    public const string UserRoleAssign = "user.role.assign";
    public const string RolePermissionAssign = "role.permission.assign";

    public const string RoleRead = "role.read";
    public const string RoleWrite = "role.write";
    public const string PermissionRead = "permission.read";
    public const string PermissionWrite = "permission.write";

    public const string IncidentCreate = "incident.create";

    /// <summary>Đọc sự cố mà mình là người báo cáo hoặc người được giao xử lý.</summary>
    public const string IncidentRead = "incident.read";

    /// <summary>
    /// Đọc mọi sự cố, kể cả của người khác. Tách khỏi <see cref="IncidentRead"/> vì từ khi
    /// khách hàng tự đăng nhập gửi ticket, "được đọc" và "được đọc tất cả" là hai quyền khác
    /// nhau: thiếu ranh giới này thì một khách hàng đọc được sự cố của mọi khách hàng khác.
    /// </summary>
    public const string IncidentReadAll = "incident.read.all";

    public const string IncidentUpdateStatus = "incident.update_status";
    public const string IncidentResolve = "incident.resolve";
    public const string IncidentAssign = "incident.assign";

    /// <summary>
    /// Đọc ghi chú nội bộ trong lịch sử chuyển trạng thái sự cố.
    ///
    /// Tách khỏi <see cref="IncidentReadAll"/> vì hai điều đó không đi liền nhau: khách hàng được
    /// xem mọi sự cố (bảng tình hình chung), nhưng ô ghi chú là chỗ nhân viên viết phân tích nội
    /// bộ và ô nhập không hề báo rằng nó sẽ hiện cho khách. Gộp làm một thì cấp quyền xem tổng
    /// quan là vô tình mở luôn phần ghi chú.
    ///
    /// Module Ticket vốn đã tách sẵn kiểu này bằng <see cref="TicketInternalNote"/>.
    /// </summary>
    public const string IncidentNoteRead = "incident.note.read";
    public const string IncidentDelete = "incident.delete";

    /// <summary>
    /// Thao tác trên sự cố dù không phải người được giao. Không có quyền này thì
    /// <c>incident.update_status</c> chỉ dùng được trên sự cố của chính mình hoặc sự cố
    /// chưa ai nhận — đây là hàng rào chống BOLA (BR-BIZ-10).
    /// </summary>
    public const string IncidentManageAny = "incident.manage_any";

    /// <summary>
    /// Viết bình luận trên sự cố mình đọc được — kênh trao đổi hai chiều giữa khách hàng và
    /// người xử lý (UC-BIZ-09). Tách khỏi <see cref="IncidentUpdateStatus"/> vì trò chuyện
    /// không phải là thao tác vòng đời: khách hàng được nói nhưng không được chuyển trạng thái.
    /// </summary>
    public const string IncidentComment = "incident.comment";

    public const string FeedbackCreate = "feedback.create";

    /// <summary>Đọc phản hồi do chính mình gửi.</summary>
    public const string FeedbackRead = "feedback.read";

    /// <summary>Đọc mọi phản hồi kèm thông tin liên hệ khách hàng (PII).</summary>
    public const string FeedbackReadAll = "feedback.read.all";

    public const string FeedbackLink = "feedback.link";

    /// <summary>
    /// Trả lời phản hồi khách hàng nhân danh doanh nghiệp — chuyển feedback sang
    /// <c>Responded</c>. Quyền của phía doanh nghiệp; khách hàng đọc câu trả lời qua
    /// <see cref="FeedbackRead"/> trên chính phản hồi của mình.
    /// </summary>
    public const string FeedbackRespond = "feedback.respond";

    // ---------------------------------------------------------------------------
    // Module Tickets — Architecture v3.1, ma trận quyền mục 10.1.
    // Read: ticket.read / ticket.create / ticket.comment · Triage: ticket.triage
    // Write: ticket.write, label.write, milestone.write, board.write · Admin: ticket.delete,
    // webhook.manage, issue_type.manage, project.manage, sla.manage.
    // ---------------------------------------------------------------------------

    /// <summary>Xem ticket, timeline PUBLIC, tìm kiếm (GitHub: Read).</summary>
    public const string TicketRead = "ticket.read";

    /// <summary>Tạo ticket (blank hoặc từ template) (GitHub: Read).</summary>
    public const string TicketCreate = "ticket.create";

    /// <summary>Comment, reaction, subscribe; sửa/xoá comment và ticket của mình (GitHub: Read).</summary>
    public const string TicketComment = "ticket.comment";

    /// <summary>Label/milestone/assignee, đóng/mở mọi ticket, duplicate, sub-issue, dependency (GitHub: Triage).</summary>
    public const string TicketTriage = "ticket.triage";

    /// <summary>Lock, pin, transfer, ẩn/sửa/xoá comment người khác, đổi type, template (GitHub: Write).</summary>
    public const string TicketWrite = "ticket.write";

    /// <summary>Xoá ticket vĩnh viễn (tombstone) (GitHub: Admin).</summary>
    public const string TicketDelete = "ticket.delete";

    /// <summary>Mở rộng riêng: ghi chú nội bộ ẩn với Customer.</summary>
    public const string TicketInternalNote = "ticket.internal_note";

    public const string LabelWrite = "label.write";
    public const string MilestoneWrite = "milestone.write";
    public const string BoardWrite = "board.write";
    public const string IssueTypeManage = "issue_type.manage";
    public const string WebhookManage = "webhook.manage";
    public const string ProjectManage = "project.manage";

    /// <summary>
    /// Thêm-bớt thành viên của một project và đặt vai trò của họ **trong project đó**.
    ///
    /// Cố ý tách khỏi <c>user.create</c>/<c>user.update</c>/<c>user.delete</c>: bảng <c>users</c>
    /// là toàn cục, một tài khoản dùng chung cho mọi project. Cho manager của project A quyền xoá
    /// tài khoản thì họ vô hiệu hoá được người đang làm ở project B, C — nơi họ không có quyền
    /// nào. Đó là leo thang đặc quyền đi vòng.
    /// </summary>
    public const string ProjectMemberManage = "project.member.manage";
    public const string SlaManage = "sla.manage";

    /// <summary>Permission code + mô tả, dùng cho seed dữ liệu tham chiếu (mục 5.7).</summary>
    public static readonly IReadOnlyDictionary<string, string> Catalog = new Dictionary<string, string>
    {
        [UserCreate] = "Tạo tài khoản",
        [UserRead] = "Xem danh sách và chi tiết user",
        [UserUpdate] = "Cập nhật thông tin user",
        [UserDelete] = "Xóa tài khoản chưa phát sinh dữ liệu nghiệp vụ",
        [UserRoleAssign] = "Gán hoặc gỡ role cho user",
        [RolePermissionAssign] = "Gán hoặc gỡ permission cho role",
        [RoleRead] = "Xem danh sách role",
        [RoleWrite] = "Tạo, sửa, xóa role",
        [PermissionRead] = "Xem danh mục permission",
        [PermissionWrite] = "Tạo, sửa, xóa permission",
        [IncidentCreate] = "Ghi nhận sự cố mới",
        [IncidentRead] = "Tra cứu sự cố mình báo cáo hoặc được giao, kèm lịch sử",
        [IncidentReadAll] = "Tra cứu mọi sự cố của mọi người",
        [IncidentUpdateStatus] = "Chuyển sự cố sang Mitigating",
        [IncidentResolve] = "Đóng sự cố sang Resolved",
        [IncidentAssign] = "Gán người xử lý",
        [IncidentNoteRead] = "Đọc ghi chú nội bộ trong lịch sử trạng thái",
        [IncidentManageAny] = "Thao tác trên sự cố không do mình phụ trách",
        [IncidentComment] = "Trao đổi trên sự cố mình đọc được",
        [IncidentDelete] = "Xóa mềm sự cố đã đóng",
        [FeedbackCreate] = "Ghi nhận phản hồi khách hàng",
        [FeedbackRead] = "Xem phản hồi do chính mình gửi",
        [FeedbackReadAll] = "Xem mọi phản hồi kèm thông tin liên hệ",
        [FeedbackLink] = "Gắn phản hồi vào sự cố",
        [FeedbackRespond] = "Trả lời phản hồi khách hàng",
        [TicketRead] = "Xem ticket, timeline công khai và tìm kiếm",
        [TicketCreate] = "Tạo ticket mới",
        [TicketComment] = "Bình luận, reaction, theo dõi ticket; sửa nội dung của mình",
        [TicketTriage] = "Gắn label/milestone/assignee, đóng/mở, duplicate, sub-issue, dependency",
        [TicketWrite] = "Khoá, pin, chuyển project, kiểm duyệt comment, đổi loại ticket, template",
        [TicketDelete] = "Xoá ticket vĩnh viễn",
        [TicketInternalNote] = "Ghi chú nội bộ ẩn với khách hàng",
        [LabelWrite] = "Tạo, sửa, xoá label",
        [MilestoneWrite] = "Tạo, sửa, xoá milestone (đi kèm label.write — cùng một màn hình)",
        [BoardWrite] = "Tạo, sửa board và cột",
        [IssueTypeManage] = "Quản lý Issue Type toàn hệ thống",
        [WebhookManage] = "Đăng ký và quản lý webhook",
        [ProjectManage] = "Tạo và cấu hình project",
        [ProjectMemberManage] = "Quản lý thành viên của project",
        [SlaManage] = "Cấu hình SLA policy"
    };

    /// <summary>Quyền của từng mức GitHub (mục 10.1) — role mặc định ghép từ đây.</summary>
    public static readonly string[] TicketReadLevel = { TicketRead, TicketCreate, TicketComment };
    public static readonly string[] TicketTriageLevel = { TicketTriage, TicketInternalNote };
    public static readonly string[] TicketWriteLevel = { TicketWrite, LabelWrite, MilestoneWrite, BoardWrite };
    public static readonly string[] TicketAdminLevel = { TicketDelete, IssueTypeManage, WebhookManage, ProjectManage, SlaManage };

    /// <summary>
    /// Vai trò đã bỏ, kèm vai trò nhận thay người đang mang nó.
    ///
    /// Dùng ở hai nơi và phải là **cùng một danh sách**: bước gỡ lúc khởi động
    /// (<c>DatabaseInitializer.RetireRolesAsync</c>) và bước chặn tạo lại
    /// (<c>RbacService.CreateRoleAsync</c>). Thiếu vế thứ hai thì một admin tạo vai trò trùng tên
    /// sẽ bị lần khởi động kế tiếp **xoá mất vai trò đó** và dồn người dùng sang vai trò thay
    /// thế — mất dữ liệu mà không ai làm gì sai.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RetiredRoles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // `viewer` chỉ khác `customer` ở chỗ xem được mọi sự cố, mà quyền đó đã chuyển thành
            // `incident.read.all` cấp riêng. Giữ hai vai trò cho cùng một việc là thừa.
            ["viewer"] = "customer",
        };

    /// <summary>
    /// Vai trò mà người dùng <b>tự chọn</b> project để tham gia.
    ///
    /// Với các vai trò này, một hàng trong <c>project_role_access</c> chỉ mở project ra thành
    /// **danh mục** chứ chưa cho vào: người dùng còn phải tự nhận (<c>project_subscriptions</c>).
    /// Mô phỏng đúng đời thật — chỉ nêu được sự cố của dịch vụ mình đang dùng.
    ///
    /// Chỉ áp cho vai trò ở đây, không áp cho mọi vai trò: cấp <c>role-access</c> cho `support`
    /// rồi cả nhóm vẫn không vào được cho tới khi từng người tự tick là hành vi không ai ngờ tới.
    /// Nhân viên được cấp thì vào được ngay.
    /// </summary>
    /// Kiểu là <c>IReadOnlyList</c> chứ không phải <c>HashSet</c> vì phép kiểm này chạy **trong
    /// câu truy vấn**: <c>Contains</c> của tập hợp là phương thức thực thể, EF không dịch được và
    /// sẽ ném ngay lúc chạy; qua danh sách thì nó thành một mệnh đề <c>IN</c>. Tên vai trò trong
    /// cơ sở dữ liệu luôn viết thường nên so khớp thẳng là đủ.
    public static readonly IReadOnlyList<string> SelfServiceRoles = new[] { "customer" };

    /// <summary>Năm role dựng sẵn. `viewer` đã bỏ — xem <see cref="RetiredRoles"/>.</summary>
    /// <summary>Tên vai trò quản trị — dùng cho hàng rào "luôn còn ít nhất một admin".</summary>
    public const string AdminRole = "admin";

    /// <summary>
    /// Vai trò đứng <b>trên</b> admin, giữ các cấu hình nền của cả hệ thống.
    ///
    /// Ranh giới: admin vận hành hệ thống — tài khoản, RBAC, project, dữ liệu. `system` giữ những
    /// thứ **không thuộc về ai** mà mọi project đều chịu ảnh hưởng: chính sách SLA và bộ loại
    /// issue. Đó đúng là hai endpoint mà mã nguồn vẫn tự khai là "cấu hình toàn hệ thống", và
    /// chúng vốn đã chỉ nhận quyền toàn cục.
    ///
    /// Tách ra vì hai lý do khác nhau về bản chất: đổi chính sách SLA là đổi cam kết dịch vụ của
    /// **mọi** project cùng lúc, còn thêm một admin là việc vận hành thường ngày. Gộp chung thì
    /// mỗi lần cần thêm một quản trị viên là trao luôn quyền đổi cam kết dịch vụ.
    /// </summary>
    public const string SystemRole = "system";

    /// <summary>
    /// Thang cấp của 5 vai trò dựng sẵn (BR-SEC-08). Khoảng cách 20–40 điểm để còn chỗ chèn
    /// vai trò tự tạo vào giữa mà không phải đánh số lại toàn bộ.
    /// </summary>
    public const int AdminRank = 100;

    /// <summary>Trên admin. Chừa khoảng trống 100–120 cho vai trò tự tạo chèn vào giữa.</summary>
    public const int SystemRank = 120;

    /// <summary>
    /// Quản lý trong phạm vi các project mình có. 80 nằm đúng khoảng trống mà thang cấp đã chừa
    /// sẵn giữa responder (60) và admin (100).
    /// </summary>
    public const int ManagerRank = 80;

    public const int ResponderRank = 60;
    public const int SupportRank = 40;
    public const int CustomerRank = 10;

    /// <summary>
    /// Cấu hình nền toàn hệ thống — chỉ <see cref="SystemRole"/> có.
    ///
    /// Hai thứ này không thuộc project nào và đổi một lần là đổi cho tất cả, nên chúng rời khỏi
    /// admin. Admin đang chạy <b>không mất gì</b> tại thời điểm bật: bước di trú cấp thêm vai trò
    /// `system` cho mọi tài khoản đang mang `admin`. Thu hẹp lại là việc của con người.
    /// </summary>
    public static readonly string[] SystemConfigLevel = { SlaManage, IssueTypeManage };

    /// <summary>Quyền của admin — tách ra thành biến để <see cref="SystemRole"/> kế thừa trọn vẹn.</summary>
    private static readonly string[] AdminPermissions =
    {
        UserCreate, UserRead, UserUpdate, UserDelete, UserRoleAssign, RolePermissionAssign,
        RoleRead, RoleWrite, PermissionRead, PermissionWrite,
        IncidentCreate, IncidentRead, IncidentReadAll, IncidentNoteRead, IncidentUpdateStatus,
        IncidentResolve, IncidentAssign, IncidentManageAny, IncidentComment, IncidentDelete,
        FeedbackCreate, FeedbackRead, FeedbackReadAll, FeedbackLink, FeedbackRespond,
        TicketRead, TicketCreate, TicketComment, TicketTriage, TicketInternalNote,
        TicketWrite, LabelWrite, MilestoneWrite, BoardWrite,
        TicketDelete, WebhookManage, ProjectManage, ProjectMemberManage
    };

    public static readonly IReadOnlyDictionary<string, (string Description, int Rank, string[] Codes)> DefaultRoles =
        new Dictionary<string, (string, int, string[])>
        {
            // `system` là admin cộng thêm cấu hình nền: đứng trên thì phải làm được mọi việc của
            // cấp dưới, nếu không thì nó là một vai trò song song chứ không phải cấp trên.
            ["system"] = ("Cấu hình nền hệ thống — chính sách SLA, loại issue, và mọi quyền của admin",
                SystemRank, AdminPermissions.Concat(SystemConfigLevel).ToArray()),

            ["admin"] = ("Quản trị viên hệ thống — tài khoản, RBAC, project và vận hành",
                AdminRank, AdminPermissions),
            ["support"] = ("Nhân viên hỗ trợ — ghi nhận sự cố và phản hồi khách hàng", SupportRank, new[]
            {
                IncidentCreate, IncidentRead, IncidentReadAll, IncidentNoteRead, IncidentComment,
                FeedbackCreate, FeedbackRead, FeedbackReadAll, FeedbackLink, FeedbackRespond,
                // GitHub Triage
                TicketRead, TicketCreate, TicketComment, TicketTriage, TicketInternalNote
            }),
            // Manager = mọi quyền của responder, cộng quyền quản lý thành viên project và các
            // quyền cấu hình trong phạm vi project mình phụ trách. KHÔNG có user.create/delete:
            // xem chú thích ở ProjectMemberManage.
            ["manager"] = ("Quản lý project — điều phối công việc và thành viên trong project mình phụ trách", ManagerRank, new[]
            {
                // Cố ý KHÔNG có UserRead: tìm người để thêm vào project đi qua
                // `GET /api/projects/{slug}/member-candidates` — nằm trong phạm vi project, thay
                // vì mở toàn bộ danh bạ nội bộ.
                IncidentRead, IncidentReadAll, IncidentNoteRead, IncidentUpdateStatus, IncidentResolve,
                IncidentAssign, IncidentManageAny, IncidentComment,
                FeedbackRead, FeedbackReadAll, FeedbackRespond, FeedbackLink,
                TicketRead, TicketCreate, TicketComment, TicketTriage, TicketInternalNote,
                TicketWrite, LabelWrite, MilestoneWrite, BoardWrite,
                // Cố ý KHÔNG có IssueTypeManage: bộ loại issue là cấu hình toàn hệ thống, endpoint
                // của nó chỉ nhận quyền toàn cục nên quyền này chưa bao giờ dùng được ở đây —
                // giữ lại chỉ là một dòng nói dối trong bảng phân quyền.
                TicketDelete, ProjectManage, ProjectMemberManage
            }),

            ["responder"] = ("Kỹ thuật viên xử lý — điều tra, khắc phục và đóng sự cố", ResponderRank, new[]
            {
                IncidentRead, IncidentReadAll, IncidentNoteRead, IncidentUpdateStatus, IncidentResolve,
                IncidentComment, FeedbackRead, FeedbackReadAll,
                // GitHub Write (bao gồm Triage)
                TicketRead, TicketCreate, TicketComment, TicketTriage, TicketInternalNote,
                TicketWrite, LabelWrite, MilestoneWrite, BoardWrite
            }),
            // ACT-BIZ-03. Khách hàng đăng nhập để tự gửi ticket và phản hồi, và chỉ thấy đúng
            // những gì mình đã gửi: có IncidentRead nhưng KHÔNG có IncidentReadAll.
            ["customer"] = ("Khách hàng — tự gửi sự cố, phản hồi và theo dõi đúng phần của mình", CustomerRank, new[]
            {
                // `incident.read.all` cho khách hàng: họ xem được **mọi sự cố trong project mà
                // vai trò customer được cấp** — không phải mọi sự cố trong hệ thống, vì phân
                // quyền theo project đã cắt sẵn phạm vi.
                //
                // Ghi chú nội bộ của nhân viên vẫn kín: nó khoá theo `incident.note.read`, một
                // permission riêng mà customer không có. Gộp hai điều đó vào một quyền thì cấp
                // quyền xem tổng quan là vô tình mở luôn phần ghi chú.
                IncidentCreate, IncidentRead, IncidentReadAll, IncidentComment,
                // FeedbackLink: khách tự gắn phản hồi của mình vào sự cố của mình. An toàn được
                // là nhờ LinkAsync soát cả hai đầu — phản hồi phải của mình, sự cố phải đọc được;
                // khách không có FeedbackReadAll nên không chạm được phản hồi của người khác.
                FeedbackCreate, FeedbackRead, FeedbackLink,
                // GitHub Read
                TicketRead, TicketCreate, TicketComment
            })
        };
}
