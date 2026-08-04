using IncidentTracker.Api.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IncidentTracker.Api.Persistence;

/// <summary>
/// CMP-05 — cổng persistence duy nhất (Hình 6). Mọi ràng buộc dữ liệu của mục 5.5
/// được khai báo ở đây để migration sinh ra đúng constraint trên PostgreSQL.
/// </summary>
public partial class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        // Incident có query filter còn IncidentStatusHistory thì cố ý không có — đây là quyết định
        // của ADR-003 để lịch sử vẫn đọc được sau khi sự cố bị xóa mềm, nên tắt cảnh báo tương ứng
        // thay vì gắn filter và làm mất bằng chứng đo SLA.
        options.ConfigureWarnings(w => w.Ignore(
            Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId
                .PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<ProjectRoleAccess> ProjectRoleAccess => Set<ProjectRoleAccess>();
    public DbSet<ProjectSubscription> ProjectSubscriptions => Set<ProjectSubscription>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentStatusHistory> IncidentStatusHistory => Set<IncidentStatusHistory>();
    public DbSet<IncidentComment> IncidentComments => Set<IncidentComment>();
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<FeedbackReply> FeedbackReplies => Set<FeedbackReply>();
    public DbSet<ContentRevision> ContentRevisions => Set<ContentRevision>();
    public DbSet<EditClaim> EditClaims => Set<EditClaim>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("pgcrypto");

        // Enum lưu dạng text để check constraint đọc được và dump SQL tự giải thích.
        var statusConverter = new EnumToStringConverter<IncidentStatus>();
        var severityConverter = new EnumToStringConverter<IncidentSeverity>();
        var channelConverter = new EnumToStringConverter<FeedbackChannel>();
        var feedbackStatusConverter = new EnumToStringConverter<FeedbackStatus>();
        var revisionEntityConverter = new EnumToStringConverter<EditableEntityType>();

        // ---------- ENT-User ----------
        b.Entity<User>(e =>
        {
            e.ToTable("users", t =>
            {
                t.HasCheckConstraint("ck_users_display_name_length",
                    "char_length(display_name) between 1 and 150");
                t.HasCheckConstraint("ck_users_login_format", "login ~ '^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,38}$'");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").HasMaxLength(150).IsRequired();
            // v3.1: tên ngắn cho @mention (mục 5.1). Unique, chữ thường.
            e.Property(x => x.Login).HasColumnName("login").HasMaxLength(39).IsRequired();
            e.HasIndex(x => x.Login).IsUnique().HasDatabaseName("ux_users_login");
            e.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired();
            // ValueGeneratedNever: giữ DEFAULT ở DB cho INSERT thủ công, nhưng buộc EF luôn gửi
            // cột này. Nếu không, tạo user với IsActive = false (đúng CLR default) sẽ bị EF bỏ
            // qua và DB điền true — sai ý định của caller.
            e.Property(x => x.IsActive).HasColumnName("is_active")
                .HasDefaultValue(true).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.AvatarKey).HasColumnName("avatar_key").HasMaxLength(400);
            e.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(32);
            e.Property(x => x.Biography).HasColumnName("biography").HasMaxLength(1000);
            // ValueGeneratedNever vì cùng lý do với is_active: giá trị false đúng ý caller không
            // được để EF bỏ qua rồi DB điền mặc định.
            e.Property(x => x.EmailVisible).HasColumnName("email_visible")
                .HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.PhoneVisible).HasColumnName("phone_visible")
                .HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.PasswordChangedAt).HasColumnName("password_changed_at");
            // Lưu bằng tên chứ không bằng số: đọc thẳng trong DB hiểu ngay, và thêm giá trị
            // mới về sau không làm lệch ý nghĩa của các hàng cũ.
            e.Property(x => x.PresenceStatus).HasColumnName("presence_status")
                .HasConversion<string>().HasMaxLength(20)
                .HasDefaultValue(Modules.Identity.PresenceStatus.Offline).ValueGeneratedNever();
            // BR-01: email duy nhất sau khi chuẩn hóa lowercase — chuẩn hóa ở service, unique ở DB.
            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("ux_users_email");
        });

        // ---------- ENT-Role ----------
        b.Entity<Role>(e =>
        {
            // Thang cấp ép ở tầng DB để một câu UPDATE tay cũng không tạo được role vượt admin.
            e.ToTable("roles", t => t.HasCheckConstraint("ck_roles_rank_range", "\"rank\" between 1 and 1000"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.IsGlobal).HasColumnName("is_global")
                .HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.Rank).HasColumnName("rank").HasDefaultValue(10).IsRequired();
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_roles_name");
        });

        // ---------- ENT-Permission ----------
        b.Entity<Permission>(e =>
        {
            // BR-02: code phải theo mẫu resource.action, ép ở tầng DB để không phụ thuộc app.
            e.ToTable("permissions", t => t.HasCheckConstraint("ck_permissions_code_format",
                PermissionCodePattern));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Code).HasColumnName("code").HasMaxLength(150).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ux_permissions_code");
        });

        // ---------- ENT-UserRole (BR-03: composite PK chống trùng cặp) ----------
        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.HasOne(x => x.User).WithMany(u => u.UserRoles)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany(r => r.UserRoles)
                .HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.RoleId).HasDatabaseName("ix_user_roles_role_id");
        });

        // ---------- Cấp quyền theo project ----------
        b.Entity<ProjectMember>(e =>
        {
            e.ToTable("project_members");
            e.HasKey(x => new { x.ProjectId, x.UserId, x.RoleId });
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.AddedAt).HasColumnName("added_at").HasDefaultValueSql("now()");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            // Middleware nạp quyền theo user mỗi request, nên đây là chiều tra nóng nhất.
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_project_members_user_id");
        });

        b.Entity<ProjectRoleAccess>(e =>
        {
            e.ToTable("project_role_access");
            e.HasKey(x => new { x.ProjectId, x.RoleId });
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.AddedAt).HasColumnName("added_at").HasDefaultValueSql("now()");
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.RoleId).HasDatabaseName("ix_project_role_access_role_id");
        });

        b.Entity<ProjectSubscription>(e =>
        {
            e.ToTable("project_subscriptions");
            e.HasKey(x => new { x.ProjectId, x.UserId });
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.JoinedAt).HasColumnName("joined_at").HasDefaultValueSql("now()");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_project_subscriptions_user_id");
        });

        // ---------- ENT-RolePermission (BR-03) ----------
        b.Entity<RolePermission>(e =>
        {
            e.ToTable("role_permissions");
            e.HasKey(x => new { x.RoleId, x.PermissionId });
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.PermissionId).HasColumnName("permission_id");
            e.HasOne(x => x.Role).WithMany(r => r.RolePermissions)
                .HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Permission).WithMany(p => p.RolePermissions)
                .HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.PermissionId).HasDatabaseName("ix_role_permissions_permission_id");
        });

        // ---------- ENT-Incident ----------
        b.Entity<Incident>(e =>
        {
            e.ToTable("incidents", t =>
            {
                t.HasCheckConstraint("ck_incidents_title_length",
                    "char_length(title) between 5 and 255");
                t.HasCheckConstraint("ck_incidents_status",
                    "status in ('Investigating','Mitigating','Resolved')");
                t.HasCheckConstraint("ck_incidents_severity",
                    "severity in ('Low','Medium','High','Critical')");
                // Invariant mục 5.3: resolved_at/resolved_by có giá trị khi và chỉ khi status = Resolved.
                t.HasCheckConstraint("ck_incidents_resolved_pairing",
                    "(status = 'Resolved' and resolved_at is not null and resolved_by is not null) "
                    + "or (status <> 'Resolved' and resolved_at is null and resolved_by is null)");
                // BR-BIZ-05: chỉ sự cố đã đóng mới được xóa mềm.
                t.HasCheckConstraint("ck_incidents_soft_delete_requires_resolved",
                    "is_deleted = false or status = 'Resolved'");
                // mitigating_at chỉ có giá trị từ bước Mitigating trở đi.
                t.HasCheckConstraint("ck_incidents_mitigating_at",
                    "mitigating_at is null or status in ('Mitigating','Resolved')");
                t.HasCheckConstraint("ck_incidents_last_edit_pairing", LastEditPairing);
            });

            e.HasKey(x => x.Id);
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.ProjectId).HasDatabaseName("ix_incidents_project_id");
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(255).IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            // ValueGeneratedNever cho cả hai enum: CLR default của IncidentSeverity là Low và của
            // IncidentStatus là Investigating — trùng sentinel của EF. Không có nó, một sự cố
            // severity = Low sẽ bị ghi thành Medium.
            e.Property(x => x.Severity).HasColumnName("severity").HasMaxLength(10)
                .HasConversion(severityConverter)
                .HasDefaultValue(IncidentSeverity.Medium).ValueGeneratedNever().IsRequired();
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20)
                .HasConversion(statusConverter)
                .HasDefaultValue(IncidentStatus.Investigating).ValueGeneratedNever().IsRequired();
            e.Property(x => x.ReporterId).HasColumnName("reporter_id");
            e.Property(x => x.AssigneeId).HasColumnName("assignee_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.MitigatingAt).HasColumnName("mitigating_at");
            e.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            e.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted")
                .HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.LastEditedAt).HasColumnName("last_edited_at");
            e.Property(x => x.LastEditedBy).HasColumnName("last_edited_by");
            e.Property(x => x.Version).HasColumnName("version")
                .HasDefaultValue(0).ValueGeneratedNever();

            e.HasOne(x => x.LastEditor).WithMany()
                .HasForeignKey(x => x.LastEditedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Reporter).WithMany()
                .HasForeignKey(x => x.ReporterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Assignee).WithMany()
                .HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Resolver).WithMany()
                .HasForeignKey(x => x.ResolvedBy).OnDelete(DeleteBehavior.Restrict);

            // ADR-003: mọi truy vấn danh sách tự động loại bản ghi đã xóa mềm.
            e.HasQueryFilter(x => !x.IsDeleted);

            // Mục 5.6 — partial index cho bảng điều khiển sự cố đang mở.
            e.HasIndex(x => new { x.Status, x.CreatedAt })
                .HasDatabaseName("ix_incidents_status_created_at")
                .HasFilter("is_deleted = false");
            // Mục 5.6 — màn hình "Việc của tôi".
            e.HasIndex(x => new { x.AssigneeId, x.Status })
                .HasDatabaseName("ix_incidents_assignee_status")
                .HasFilter("is_deleted = false");
        });

        // ---------- ENT-IncidentStatusHistory ----------
        b.Entity<IncidentStatusHistory>(e =>
        {
            e.ToTable("incident_status_history", t =>
            {
                t.HasCheckConstraint("ck_history_from_status",
                    "from_status in ('Investigating','Mitigating','Resolved')");
                t.HasCheckConstraint("ck_history_to_status",
                    "to_status in ('Investigating','Mitigating','Resolved')");
                // BR-BIZ-02: cấm chuyển trùng trạng thái, chặn luôn ở tầng DB.
                t.HasCheckConstraint("ck_history_status_changed", "from_status <> to_status");
            });

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.IncidentId).HasColumnName("incident_id");
            e.Property(x => x.FromStatus).HasColumnName("from_status").HasMaxLength(20)
                .HasConversion(statusConverter).IsRequired();
            e.Property(x => x.ToStatus).HasColumnName("to_status").HasMaxLength(20)
                .HasConversion(statusConverter).IsRequired();
            e.Property(x => x.ChangedBy).HasColumnName("changed_by");
            e.Property(x => x.ChangedAt).HasColumnName("changed_at").HasDefaultValueSql("now()");
            e.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);

            e.HasOne(x => x.Incident).WithMany(i => i.StatusHistory)
                .HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ChangedByUser).WithMany()
                .HasForeignKey(x => x.ChangedBy).OnDelete(DeleteBehavior.Restrict);

            // ADR-003: lịch sử vẫn phải đọc được sau khi incident bị xóa mềm nên KHÔNG gắn
            // query filter theo Incident — nếu gắn, EF sẽ lọc mất bằng chứng đo SLA.
            e.HasIndex(x => new { x.IncidentId, x.ChangedAt })
                .HasDatabaseName("ix_history_incident_changed_at");
        });

        // ---------- ENT-IncidentComment ----------
        b.Entity<IncidentComment>(e =>
        {
            e.ToTable("incident_comments", t =>
            {
                t.HasCheckConstraint(
                    "ck_incident_comments_body_length", "char_length(body) between 1 and 2000");
                t.HasCheckConstraint("ck_incident_comments_last_edit_pairing", LastEditPairing);
            });

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.IncidentId).HasColumnName("incident_id");
            e.Property(x => x.AuthorId).HasColumnName("author_id");
            e.Property(x => x.Body).HasColumnName("body").HasMaxLength(2000).IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.LastEditedAt).HasColumnName("last_edited_at");
            e.Property(x => x.LastEditedBy).HasColumnName("last_edited_by");
            e.Property(x => x.Version).HasColumnName("version")
                .HasDefaultValue(0).ValueGeneratedNever();

            e.HasOne(x => x.LastEditor).WithMany()
                .HasForeignKey(x => x.LastEditedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Incident).WithMany(i => i.Comments)
                .HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Author).WithMany()
                .HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);

            // Cùng lý do với IncidentStatusHistory: hội thoại vẫn đọc được sau khi sự cố bị
            // xóa mềm, nên không gắn query filter theo Incident.
            e.HasIndex(x => new { x.IncidentId, x.CreatedAt })
                .HasDatabaseName("ix_incident_comments_incident_created_at");
        });

        // ---------- ENT-Feedback ----------
        b.Entity<Feedback>(e =>
        {
            e.ToTable("feedbacks", t =>
            {
                t.HasCheckConstraint("ck_feedbacks_channel",
                    "channel in ('Email','Hotline','Web','Other')");
                t.HasCheckConstraint("ck_feedbacks_status",
                    "status in ('New','Acknowledged','Responded')");
                t.HasCheckConstraint("ck_feedbacks_content_length", "char_length(content) >= 10");
                t.HasCheckConstraint("ck_feedbacks_customer_email_format",
                    "customer_email is null or customer_email ~ " + EmailPattern);
                t.HasCheckConstraint("ck_feedbacks_last_edit_pairing", LastEditPairing);
            });

            e.HasKey(x => x.Id);
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.ProjectId).HasDatabaseName("ix_feedbacks_project_id");
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(20)
                .HasConversion(channelConverter).IsRequired();
            e.Property(x => x.CustomerEmail).HasColumnName("customer_email").HasMaxLength(320);
            e.Property(x => x.Content).HasColumnName("content").IsRequired();
            // ValueGeneratedNever cùng lý do với IncidentStatus: FeedbackStatus.New = 0 trùng
            // sentinel của EF, thiếu nó thì feedback mới sẽ bị DB điền nhầm default.
            e.Property(x => x.Status).HasColumnName("status").HasMaxLength(20)
                .HasConversion(feedbackStatusConverter)
                .HasDefaultValue(FeedbackStatus.New).ValueGeneratedNever().IsRequired();
            e.Property(x => x.IncidentId).HasColumnName("incident_id");
            e.Property(x => x.CreatedBy).HasColumnName("created_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.LastEditedAt).HasColumnName("last_edited_at");
            e.Property(x => x.LastEditedBy).HasColumnName("last_edited_by");
            e.Property(x => x.Version).HasColumnName("version")
                .HasDefaultValue(0).ValueGeneratedNever();

            e.HasOne(x => x.LastEditor).WithMany()
                .HasForeignKey(x => x.LastEditedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Incident).WithMany(i => i.Feedbacks)
                .HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.CreatedByUser).WithMany()
                .HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);

            // Mục 5.6 — partial index cho hàng đợi phản hồi chưa phân loại.
            e.HasIndex(x => x.CreatedAt)
                .HasDatabaseName("ix_feedbacks_unclassified")
                .HasFilter("incident_id is null");
            e.HasIndex(x => x.IncidentId).HasDatabaseName("ix_feedbacks_incident_id");
        });

        // ---------- ENT-FeedbackReply ----------
        b.Entity<FeedbackReply>(e =>
        {
            e.ToTable("feedback_replies", t =>
            {
                t.HasCheckConstraint("ck_feedback_replies_body_length",
                    "char_length(body) between 1 and 2000");
                // Lời trả lời hoặc do một người thật ký tên, hoặc do hệ thống sinh — không có
                // trường hợp thứ ba "vô danh".
                t.HasCheckConstraint("ck_feedback_replies_author",
                    "is_automatic = true or responder_id is not null");
                t.HasCheckConstraint("ck_feedback_replies_last_edit_pairing", LastEditPairing);
                // Lời xác nhận tự động không có tác giả nên cũng không có ai sửa được nó.
                // Ràng buộc ở tầng DB để một lỗi ở tầng service không lặng lẽ viết lại lời
                // mà hệ thống đã gửi cho khách.
                t.HasCheckConstraint("ck_feedback_replies_automatic_immutable",
                    "is_automatic = false or last_edited_at is null");
            });

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.FeedbackId).HasColumnName("feedback_id");
            e.Property(x => x.ResponderId).HasColumnName("responder_id");
            e.Property(x => x.Body).HasColumnName("body").HasMaxLength(2000).IsRequired();
            e.Property(x => x.IsAutomatic).HasColumnName("is_automatic")
                .HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.LastEditedAt).HasColumnName("last_edited_at");
            e.Property(x => x.LastEditedBy).HasColumnName("last_edited_by");
            e.Property(x => x.Version).HasColumnName("version")
                .HasDefaultValue(0).ValueGeneratedNever();

            e.HasOne(x => x.LastEditor).WithMany()
                .HasForeignKey(x => x.LastEditedBy).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Feedback).WithMany(f => f.Replies)
                .HasForeignKey(x => x.FeedbackId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Responder).WithMany()
                .HasForeignKey(x => x.ResponderId).OnDelete(DeleteBehavior.Restrict);

            e.HasIndex(x => new { x.FeedbackId, x.CreatedAt })
                .HasDatabaseName("ix_feedback_replies_feedback_created_at");
        });

        // ---------- ENT-ContentRevision ----------
        b.Entity<ContentRevision>(e =>
        {
            e.ToTable("content_revisions", t =>
            {
                t.HasCheckConstraint("ck_content_revisions_entity_type",
                    "entity_type in ('Incident','IncidentComment','Feedback','FeedbackReply')");
                t.HasCheckConstraint("ck_content_revisions_field_format",
                    "field ~ '^[a-z][a-z0-9_]*$'");
                // Một dòng lịch sử không nói được điều gì đã đổi là rác, không phải bằng chứng.
                t.HasCheckConstraint("ck_content_revisions_changed",
                    "old_value is distinct from new_value");
                t.HasCheckConstraint("ck_content_revisions_reason_length",
                    "reason is null or char_length(reason) between 1 and 500");
            });

            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(30)
                .HasConversion(revisionEntityConverter).IsRequired();
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.Field).HasColumnName("field").HasMaxLength(40).IsRequired();
            e.Property(x => x.OldValue).HasColumnName("old_value");
            e.Property(x => x.NewValue).HasColumnName("new_value");
            e.Property(x => x.EditedBy).HasColumnName("edited_by");
            e.Property(x => x.EditedAt).HasColumnName("edited_at").HasDefaultValueSql("now()");
            e.Property(x => x.OnBehalf).HasColumnName("on_behalf")
                .HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(500);

            e.HasOne(x => x.EditedByUser).WithMany()
                .HasForeignKey(x => x.EditedBy).OnDelete(DeleteBehavior.Restrict);

            // Cố ý KHÔNG có khóa ngoại tới bản ghi cha: lịch sử phải sống sót qua việc bản ghi
            // cha bị xóa, cùng lý do đã áp cho incident_status_history (ADR-003).
            e.HasIndex(x => new { x.EntityType, x.EntityId, x.EditedAt })
                .HasDatabaseName("ix_content_revisions_entity");
        });

        // ---------- ENT-EditClaim ----------
        b.Entity<EditClaim>(e =>
        {
            e.ToTable("edit_claims", t =>
            {
                t.HasCheckConstraint("ck_edit_claims_entity_type",
                    "entity_type in ('Incident','IncidentComment','Feedback','FeedbackReply')");
                // Một chỗ giữ đã hết hạn trước cả khi được ghi là vô nghĩa.
                t.HasCheckConstraint("ck_edit_claims_expiry", "expires_at > claimed_at");
            });

            // Khoá chính gồm cả người giữ: hai người cùng mở form là hai hàng, và cả hai đều
            // nhìn thấy phía kia. Xem chú thích của ENT-EditClaim.
            e.HasKey(x => new { x.EntityType, x.EntityId, x.UserId });
            e.Property(x => x.EntityType).HasColumnName("entity_type").HasMaxLength(30)
                .HasConversion(revisionEntityConverter).IsRequired();
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.ClaimedAt).HasColumnName("claimed_at").HasDefaultValueSql("now()");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");

            // Cascade chứ không Restrict: chỗ giữ là trạng thái tạm của một phiên làm việc, không
            // phải bằng chứng. Xoá tài khoản thì nó đi theo — khác hẳn content_revisions, nơi
            // chữ ký phải ở lại và vì vậy mới chặn việc xoá.
            e.HasOne(x => x.User).WithMany()
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

            // Cùng lý do với content_revisions: không có khoá ngoại tới bản ghi cha vì một bảng
            // dùng chung cho bốn loại. Hàng mồ côi vô hại — nó hết hạn rồi bị dọn.
            e.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_edit_claims_expires_at");
        });

        // ---------- Module Tickets (Architecture v3.1) ----------
        ConfigureTickets(b);

        // MassTransit EF Outbox (mục 6.2): message chỉ rời DB sau khi transaction commit; inbox
        // trên consumer endpoint chống xử lý trùng khi retry.
        b.AddInboxStateEntity();
        b.AddOutboxMessageEntity();
        b.AddOutboxStateEntity();
    }

    // Tách hằng regex ra ngoài để phần escape của PostgreSQL không lẫn với chuỗi C#.
    private const string PermissionCodePattern =
        @"code ~ '^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$'";

    /// <summary>
    /// Dấu vết sửa đổi đi theo cặp: có mốc thời gian thì phải có chữ ký, và ngược lại. Một
    /// bản ghi "đã sửa lúc 3 giờ sáng, không rõ ai" còn tệ hơn là không ghi gì.
    /// </summary>
    private const string LastEditPairing =
        "(last_edited_at is null) = (last_edited_by is null)";

    private const string EmailPattern =
        @"'^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$'";
}
