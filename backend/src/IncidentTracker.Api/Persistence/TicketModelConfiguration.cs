using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Persistence;

/// <summary>
/// Cấu hình EF Core cho module Tickets (Architecture v3.1, mục 5.1–5.3). Tách file để
/// <c>AppDbContext</c> R1 không phình; quy ước snake_case và check constraint SQL giữ nguyên.
/// </summary>
public partial class AppDbContext
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<IssueType> IssueTypes => Set<IssueType>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketEvent> TicketEvents => Set<TicketEvent>();
    public DbSet<TicketReference> TicketReferences => Set<TicketReference>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<TicketLabel> TicketLabels => Set<TicketLabel>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<TicketAssignee> TicketAssignees => Set<TicketAssignee>();
    public DbSet<TicketReaction> TicketReactions => Set<TicketReaction>();
    public DbSet<TicketSubscription> TicketSubscriptions => Set<TicketSubscription>();
    public DbSet<ProjectWatch> ProjectWatches => Set<ProjectWatch>();
    public DbSet<NotificationThread> NotificationThreads => Set<NotificationThread>();
    public DbSet<TicketTemplate> TicketTemplates => Set<TicketTemplate>();
    public DbSet<Board> Boards => Set<Board>();
    public DbSet<BoardColumn> BoardColumns => Set<BoardColumn>();
    public DbSet<BoardItem> BoardItems => Set<BoardItem>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();
    public DbSet<TicketRedirect> TicketRedirects => Set<TicketRedirect>();
    public DbSet<TicketSearchComment> TicketSearchComments => Set<TicketSearchComment>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    /// <summary>Tên sequence cấp <c>ticket_events.sequence</c> (bảng partition không dùng identity ở PG16).</summary>
    public const string TicketEventSequence = "ticket_events_sequence_seq";

    private static void ConfigureTickets(ModelBuilder b)
    {
        // ---------- projects ----------
        b.Entity<Project>(e =>
        {
            e.ToTable("projects", t =>
            {
                t.HasCheckConstraint("ck_projects_slug_format", "slug ~ '^[a-z0-9][a-z0-9._-]{0,98}$'");
                t.HasCheckConstraint("ck_projects_next_number", "next_ticket_number >= 1");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(100).IsRequired();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
            e.Property(x => x.BlankIssuesEnabled).HasColumnName("blank_issues_enabled").HasDefaultValue(true).ValueGeneratedNever();
            e.Property(x => x.StrictClosePolicy).HasColumnName("strict_close_policy").HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.AutoReopenOnCustomerComment).HasColumnName("auto_reopen_on_customer_comment").HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.CustomersSeeOnlyOwn).HasColumnName("customers_see_only_own").HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.NextTicketNumber).HasColumnName("next_ticket_number").HasDefaultValue(1).ValueGeneratedNever();
            e.Property(x => x.ContactLinks).HasColumnName("contact_links").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").ValueGeneratedNever();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.IsArchived).HasColumnName("is_archived").HasDefaultValue(false).ValueGeneratedNever();
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_projects_slug");
        });

        // ---------- issue_types ----------
        b.Entity<IssueType>(e =>
        {
            e.ToTable("issue_types");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(50).IsRequired();
            e.Property(x => x.Color).HasColumnName("color").HasMaxLength(20).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(true).ValueGeneratedNever();
            e.Property(x => x.Position).HasColumnName("position").HasDefaultValue(0).ValueGeneratedNever();
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("ux_issue_types_name");
        });

        // ---------- tickets ----------
        b.Entity<Ticket>(e =>
        {
            e.ToTable("tickets", t =>
            {
                t.HasCheckConstraint("ck_tickets_title_length", "char_length(title) between 1 and 256");
                t.HasCheckConstraint("ck_tickets_state", EnumNaming.SqlInList<TicketState>("state"));
                t.HasCheckConstraint("ck_tickets_state_reason",
                    "state_reason is null or " + EnumNaming.SqlInList<StateReason>("state_reason"));
                t.HasCheckConstraint("ck_tickets_lock_reason",
                    "active_lock_reason is null or " + EnumNaming.SqlInList<LockReason>("active_lock_reason"));
                t.HasCheckConstraint("ck_tickets_priority",
                    "priority is null or " + EnumNaming.SqlInList<TicketPriority>("priority"));
                // Đóng ⇔ có closed_at; DUPLICATE ⇒ có duplicate_of.
                t.HasCheckConstraint("ck_tickets_closed_pairing",
                    "(state = 'CLOSED' and closed_at is not null) or (state = 'OPEN' and closed_at is null)");
                t.HasCheckConstraint("ck_tickets_duplicate_pairing",
                    "state_reason <> 'DUPLICATE' or duplicate_of_ticket_id is not null");
                t.HasCheckConstraint("ck_tickets_number_positive", "number >= 1");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Number).HasColumnName("number");
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(256).IsRequired();
            e.Property(x => x.Body).HasColumnName("body").IsRequired();
            e.Property(x => x.AuthorId).HasColumnName("author_id");
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(10)
                .HasConversion(new UpperSnakeEnumConverter<TicketState>())
                .HasDefaultValue(TicketState.Open).ValueGeneratedNever().IsRequired();
            e.Property(x => x.StateReason).HasColumnName("state_reason").HasMaxLength(20)
                .HasConversion(new UpperSnakeEnumConverter<StateReason>());
            e.Property(x => x.DuplicateOfTicketId).HasColumnName("duplicate_of_ticket_id");
            e.Property(x => x.ClosedById).HasColumnName("closed_by_id");
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.IsLocked).HasColumnName("is_locked").HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.ActiveLockReason).HasColumnName("active_lock_reason").HasMaxLength(20)
                .HasConversion(new UpperSnakeEnumConverter<LockReason>());
            e.Property(x => x.IsPinned).HasColumnName("is_pinned").HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.TypeId).HasColumnName("type_id");
            e.Property(x => x.ParentTicketId).HasColumnName("parent_ticket_id");
            e.Property(x => x.SubIssuePosition).HasColumnName("sub_issue_position");
            e.Property(x => x.MilestoneId).HasColumnName("milestone_id");
            e.Property(x => x.Priority).HasColumnName("priority").HasMaxLength(4)
                .HasConversion(new UpperSnakeEnumConverter<TicketPriority>());
            e.Property(x => x.SlaDueAt).HasColumnName("sla_due_at");
            e.Property(x => x.FirstResponseAt).HasColumnName("first_response_at");
            e.Property(x => x.CommentsCount).HasColumnName("comments_count").HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.ReactionsSummary).HasColumnName("reactions_summary").HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb").ValueGeneratedNever();
            e.Property(x => x.Version).HasColumnName("version").HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            // UC-14: FTS trên title + body (cấu hình 'simple' để không phụ thuộc từ điển tiếng Việt).
            e.Property(x => x.SearchVector).HasColumnName("search_vector")
                .HasComputedColumnSql("to_tsvector('simple', coalesce(title, '') || ' ' || coalesce(body, ''))", stored: true);
            e.HasIndex(x => x.SearchVector).HasDatabaseName("ix_tickets_search_vector").HasMethod("GIN");

            e.HasOne(x => x.Project).WithMany(p => p.Tickets)
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Author).WithMany()
                .HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Type).WithMany()
                .HasForeignKey(x => x.TypeId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ParentTicket).WithMany(p => p.SubIssues)
                .HasForeignKey(x => x.ParentTicketId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Milestone).WithMany()
                .HasForeignKey(x => x.MilestoneId).OnDelete(DeleteBehavior.SetNull);

            // BR-LIFECYCLE-05: tombstone ẩn khỏi mọi truy vấn mặc định.
            e.HasQueryFilter(x => !x.IsDeleted);

            e.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique().HasDatabaseName("ux_tickets_project_number");
            e.HasIndex(x => new { x.ProjectId, x.State, x.UpdatedAt }).HasDatabaseName("ix_tickets_project_state_updated");
            e.HasIndex(x => x.ParentTicketId).HasDatabaseName("ix_tickets_parent");
            e.HasIndex(x => x.MilestoneId).HasDatabaseName("ix_tickets_milestone");
            e.HasIndex(x => x.AuthorId).HasDatabaseName("ix_tickets_author");
            e.HasIndex(x => x.SlaDueAt).HasDatabaseName("ix_tickets_sla_due").HasFilter("state = 'OPEN' and sla_due_at is not null");
            e.HasIndex(x => x.ProjectId).HasDatabaseName("ix_tickets_pinned").HasFilter("is_pinned = true");
        });

        // ---------- ticket_events (partition — tạo bằng raw SQL trong migration) ----------
        b.Entity<TicketEvent>(e =>
        {
            e.ToTable("ticket_events", t => t.ExcludeFromMigrations());
            e.HasKey(x => new { x.Sequence, x.CreatedAt });
            e.Property(x => x.Sequence).HasColumnName("sequence")
                .HasDefaultValueSql($"nextval('{TicketEventSequence}')").ValueGeneratedOnAdd();
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.TicketVersion).HasColumnName("ticket_version");
            e.Property(x => x.ActorId).HasColumnName("actor_id");
            e.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(50).IsRequired();
            e.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Visibility).HasColumnName("visibility").HasMaxLength(16)
                .HasConversion(new UpperSnakeEnumConverter<EventVisibility>()).IsRequired();
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            // Khai báo FK để EF xếp thứ tự INSERT (ticket trước event) trong cùng SaveChanges;
            // FK vật lý đã có trong SQL partition, migration không sinh lại vì ExcludeFromMigrations.
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.SetNull);
        });

        // ---------- ticket_references ----------
        b.Entity<TicketReference>(e =>
        {
            e.ToTable("ticket_references", t =>
            {
                t.HasCheckConstraint("ck_ticket_references_type", EnumNaming.SqlInList<RelationType>("relation_type"));
                t.HasCheckConstraint("ck_ticket_references_not_self", "source_id <> target_id");
            });
            e.HasKey(x => new { x.SourceId, x.TargetId, x.RelationType });
            e.Property(x => x.SourceId).HasColumnName("source_id");
            e.Property(x => x.TargetId).HasColumnName("target_id");
            e.Property(x => x.RelationType).HasColumnName("relation_type").HasMaxLength(20)
                .HasConversion(new UpperSnakeEnumConverter<RelationType>());
            e.Property(x => x.CreatedByEventId).HasColumnName("created_by_event_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.TargetId, x.RelationType }).HasDatabaseName("ix_ticket_references_target");
        });

        // ---------- labels ----------
        b.Entity<Label>(e =>
        {
            e.ToTable("labels", t =>
            {
                t.HasCheckConstraint("ck_labels_color", "color_hex ~ '^[0-9a-fA-F]{6}$'");
                t.HasCheckConstraint("ck_labels_name_length", "char_length(name) between 1 and 50");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(50).IsRequired();
            e.Property(x => x.ColorHex).HasColumnName("color_hex").HasMaxLength(6).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(200);
            e.Property(x => x.IsDefault).HasColumnName("is_default").HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.IsArchived).HasColumnName("is_archived").HasDefaultValue(false).ValueGeneratedNever();
            e.HasOne(x => x.Project).WithMany(p => p.Labels)
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            // BR-ORG-04: tên duy nhất không phân biệt hoa/thường trong project (chỉ label còn sống).
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique()
                .HasDatabaseName("ux_labels_project_lower_name")
                .HasFilter("is_archived = false");
        });

        b.Entity<TicketLabel>(e =>
        {
            e.ToTable("ticket_labels");
            e.HasKey(x => new { x.TicketId, x.LabelId });
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.LabelId).HasColumnName("label_id");
            e.HasOne(x => x.Ticket).WithMany(t => t.Labels).HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Label).WithMany().HasForeignKey(x => x.LabelId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.LabelId).HasDatabaseName("ix_ticket_labels_label");
        });

        // ---------- milestones ----------
        b.Entity<Milestone>(e =>
        {
            e.ToTable("milestones", t =>
            {
                t.HasCheckConstraint("ck_milestones_state", EnumNaming.SqlInList<TicketState>("state"));
                t.HasCheckConstraint("ck_milestones_title_length", "char_length(title) between 1 and 200");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Number).HasColumnName("number");
            e.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.DueOn).HasColumnName("due_on");
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(10)
                .HasConversion(new UpperSnakeEnumConverter<TicketState>())
                .HasDefaultValue(TicketState.Open).ValueGeneratedNever();
            e.Property(x => x.ClosedAt).HasColumnName("closed_at");
            e.Property(x => x.OpenCount).HasColumnName("open_count").HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.ClosedCount).HasColumnName("closed_count").HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            e.HasOne(x => x.Project).WithMany(p => p.Milestones)
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Number }).IsUnique().HasDatabaseName("ux_milestones_project_number");
            e.HasIndex(x => new { x.ProjectId, x.Title }).IsUnique().HasDatabaseName("ux_milestones_project_title");
        });

        // ---------- ticket_assignees ----------
        b.Entity<TicketAssignee>(e =>
        {
            e.ToTable("ticket_assignees");
            e.HasKey(x => new { x.TicketId, x.UserId });
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.AssignedAt).HasColumnName("assigned_at").HasDefaultValueSql("now()");
            e.HasOne(x => x.Ticket).WithMany(t => t.Assignees).HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_ticket_assignees_user");
        });

        // ---------- ticket_reactions ----------
        b.Entity<TicketReaction>(e =>
        {
            e.ToTable("ticket_reactions", t =>
                t.HasCheckConstraint("ck_ticket_reactions_type", EnumNaming.SqlInList<ReactionType>("reaction_type")));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.ReactionType).HasColumnName("reaction_type").HasMaxLength(20)
                .HasConversion(new UpperSnakeEnumConverter<ReactionType>());
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            // BR-SOCIAL-01: 1 reaction / loại / user / đối tượng. NULLS NOT DISTINCT để event_id null
            // (reaction trên body) cũng bị ràng buộc.
            e.HasIndex(x => new { x.TicketId, x.EventId, x.UserId, x.ReactionType }).IsUnique()
                .HasDatabaseName("ux_ticket_reactions_target_user_type")
                .AreNullsDistinct(false);
            e.HasIndex(x => x.EventId).HasDatabaseName("ix_ticket_reactions_event");
        });

        // ---------- ticket_subscriptions ----------
        b.Entity<TicketSubscription>(e =>
        {
            e.ToTable("ticket_subscriptions", t =>
            {
                t.HasCheckConstraint("ck_ticket_subscriptions_state", EnumNaming.SqlInList<SubscriptionState>("state"));
                t.HasCheckConstraint("ck_ticket_subscriptions_reason", EnumNaming.SqlInList<SubscriptionReason>("reason"));
            });
            e.HasKey(x => new { x.TicketId, x.UserId });
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.State).HasColumnName("state").HasMaxLength(20)
                .HasConversion(new UpperSnakeEnumConverter<SubscriptionState>());
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(20)
                .HasConversion(new UpperSnakeEnumConverter<SubscriptionReason>());
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_ticket_subscriptions_user");
        });

        // ---------- project_watches ----------
        b.Entity<ProjectWatch>(e =>
        {
            e.ToTable("project_watches", t =>
                t.HasCheckConstraint("ck_project_watches_level", EnumNaming.SqlInList<WatchLevel>("level")));
            e.HasKey(x => new { x.ProjectId, x.UserId });
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Level).HasColumnName("level").HasMaxLength(20)
                .HasConversion(new UpperSnakeEnumConverter<WatchLevel>());
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // ---------- notification_threads ----------
        b.Entity<NotificationThread>(e =>
        {
            e.ToTable("notification_threads", t =>
                t.HasCheckConstraint("ck_notification_threads_reason", EnumNaming.SqlInList<NotificationReason>("reason")));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(20)
                .HasConversion(new UpperSnakeEnumConverter<NotificationReason>());
            e.Property(x => x.Unread).HasColumnName("unread").HasDefaultValue(true).ValueGeneratedNever();
            e.Property(x => x.IsDone).HasColumnName("is_done").HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.IsSaved).HasColumnName("is_saved").HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.LastEventId).HasColumnName("last_event_id");
            e.Property(x => x.LastEventType).HasColumnName("last_event_type").HasMaxLength(50);
            e.Property(x => x.LastActorId).HasColumnName("last_actor_id");
            e.Property(x => x.LastReadAt).HasColumnName("last_read_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.TicketId }).IsUnique().HasDatabaseName("ux_notification_threads_user_ticket");
            e.HasIndex(x => new { x.UserId, x.Unread, x.UpdatedAt }).HasDatabaseName("ix_notification_threads_inbox");
        });

        // ---------- ticket_templates ----------
        b.Entity<TicketTemplate>(e =>
        {
            e.ToTable("ticket_templates");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(500);
            e.Property(x => x.TitlePrefix).HasColumnName("title_prefix").HasMaxLength(100);
            e.Property(x => x.Defaults).HasColumnName("defaults").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").ValueGeneratedNever();
            e.Property(x => x.BodySchema).HasColumnName("body_schema").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").ValueGeneratedNever();
            e.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(true).ValueGeneratedNever();
            e.Property(x => x.Position).HasColumnName("position").HasDefaultValue(0).ValueGeneratedNever();
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique().HasDatabaseName("ux_ticket_templates_project_name");
        });

        // ---------- boards ----------
        b.Entity<Board>(e =>
        {
            e.ToTable("boards", t =>
                t.HasCheckConstraint("ck_boards_visibility", EnumNaming.SqlInList<BoardVisibility>("visibility")));
            e.HasKey(x => x.Id);
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.ProjectId).HasDatabaseName("ix_boards_project_id");
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()").ValueGeneratedNever();
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
            e.Property(x => x.Visibility).HasColumnName("visibility").HasMaxLength(20)
                .HasConversion(new UpperSnakeEnumConverter<BoardVisibility>());
            e.Property(x => x.Automation).HasColumnName("automation").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").ValueGeneratedNever();
            e.Property(x => x.CreatedById).HasColumnName("created_by_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.IsClosed).HasColumnName("is_closed").HasDefaultValue(false).ValueGeneratedNever();
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<BoardColumn>(e =>
        {
            e.ToTable("board_columns");
            e.HasKey(x => x.Id);
            // ValueGeneratedNever: khoá do mã tự gán. Thiếu nó thì EF coi cột mới gắn vào một
            // Board đang được theo dõi là bản ghi cũ (khoá đã có giá trị) và phát UPDATE thay vì
            // INSERT — AddColumnAsync khi đó luôn ném DbUpdateConcurrencyException.
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()").ValueGeneratedNever();
            e.Property(x => x.BoardId).HasColumnName("board_id");
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            e.Property(x => x.Color).HasColumnName("color").HasMaxLength(20);
            e.Property(x => x.Position).HasColumnName("position");
            e.HasOne(x => x.Board).WithMany(b2 => b2.Columns).HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.BoardId, x.Name }).IsUnique().HasDatabaseName("ux_board_columns_board_name");
        });

        b.Entity<BoardItem>(e =>
        {
            e.ToTable("board_items", t =>
                t.HasCheckConstraint("ck_board_items_kind", "ticket_id is not null or draft_title is not null"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()").ValueGeneratedNever();
            e.Property(x => x.BoardId).HasColumnName("board_id");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.DraftTitle).HasColumnName("draft_title").HasMaxLength(256);
            e.Property(x => x.ColumnId).HasColumnName("column_id");
            e.Property(x => x.Position).HasColumnName("position");
            e.Property(x => x.AddedAt).HasColumnName("added_at").HasDefaultValueSql("now()");
            e.HasOne(x => x.Board).WithMany(b2 => b2.Items).HasForeignKey(x => x.BoardId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Ticket).WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Column).WithMany().HasForeignKey(x => x.ColumnId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.BoardId, x.TicketId }).IsUnique().HasDatabaseName("ux_board_items_board_ticket")
                .HasFilter("ticket_id is not null");
            e.HasIndex(x => new { x.BoardId, x.ColumnId, x.Position }).HasDatabaseName("ix_board_items_column_position");
        });

        // ---------- ticket_search_comments (UC-14 read model) ----------
        b.Entity<TicketSearchComment>(e =>
        {
            e.ToTable("ticket_search_comments");
            e.HasKey(x => x.TicketId);
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.PublicText).HasColumnName("public_text").IsRequired();
            e.Property(x => x.InternalText).HasColumnName("internal_text").IsRequired();
            e.Property(x => x.Commenters).HasColumnName("commenters").IsRequired();
            e.Property(x => x.Mentioned).HasColumnName("mentioned").IsRequired();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");
            e.HasOne<Ticket>().WithOne().HasForeignKey<TicketSearchComment>(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        // ---------- ticket_redirects (BR-REL-05) ----------
        b.Entity<TicketRedirect>(e =>
        {
            e.ToTable("ticket_redirects");
            e.HasKey(x => new { x.ProjectId, x.Number });
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.Number).HasColumnName("number");
            e.Property(x => x.TicketId).HasColumnName("ticket_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Ticket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        // ---------- idempotency_keys (BR-REL-01) ----------
        b.Entity<IdempotencyKey>(e =>
        {
            e.ToTable("idempotency_keys");
            e.HasKey(x => new { x.Key, x.UserId });
            e.Property(x => x.Key).HasColumnName("key").HasMaxLength(64);
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsRequired();
            e.Property(x => x.ResponseStatus).HasColumnName("response_status");
            e.Property(x => x.ResponseBody).HasColumnName("response_body");
            e.Property(x => x.ResponseContentType).HasColumnName("response_content_type").HasMaxLength(100);
            e.Property(x => x.ResponseHeaders).HasColumnName("response_headers").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_idempotency_keys_expires");
        });

        // ---------- sla_policies (mở rộng) ----------
        b.Entity<SlaPolicy>(e =>
        {
            e.ToTable("sla_policies", t =>
                t.HasCheckConstraint("ck_sla_policies_priority", EnumNaming.SqlInList<TicketPriority>("priority")));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.Priority).HasColumnName("priority").HasMaxLength(4)
                .HasConversion(new UpperSnakeEnumConverter<TicketPriority>());
            e.Property(x => x.ResponseTimeMinutes).HasColumnName("response_time_minutes");
            e.Property(x => x.ResolutionTimeMinutes).HasColumnName("resolution_time_minutes");
            e.Property(x => x.EscalateAfterMinutes).HasColumnName("escalate_after_minutes").HasDefaultValue(60).ValueGeneratedNever();
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true).ValueGeneratedNever();
            e.HasIndex(x => x.Priority).IsUnique().HasDatabaseName("ux_sla_policies_priority");
        });

        // ---------- webhooks ----------
        b.Entity<WebhookSubscription>(e =>
        {
            e.ToTable("webhook_subscriptions", t =>
                t.HasCheckConstraint("ck_webhook_subscriptions_url", "target_url ~* '^https?://'"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.ProjectId).HasColumnName("project_id");
            e.Property(x => x.TargetUrl).HasColumnName("target_url").HasMaxLength(2000).IsRequired();
            e.Property(x => x.SecretHmac).HasColumnName("secret_hmac").HasMaxLength(200).IsRequired();
            e.Property(x => x.Events).HasColumnName("events").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").ValueGeneratedNever();
            e.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true).ValueGeneratedNever();
            e.Property(x => x.ConsecutiveFailures).HasColumnName("consecutive_failures").HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.CreatedById).HasColumnName("created_by_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("webhook_deliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            e.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
            e.Property(x => x.EventId).HasColumnName("event_id");
            e.Property(x => x.EventName).HasColumnName("event_name").HasMaxLength(50).IsRequired();
            e.Property(x => x.Action).HasColumnName("action").HasMaxLength(50).IsRequired();
            e.Property(x => x.RequestBody).HasColumnName("request_body").IsRequired();
            e.Property(x => x.RequestHeaders).HasColumnName("request_headers").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb").ValueGeneratedNever();
            e.Property(x => x.HttpStatus).HasColumnName("http_status");
            e.Property(x => x.DurationMs).HasColumnName("duration_ms");
            e.Property(x => x.ResponseBody).HasColumnName("response_body");
            e.Property(x => x.Error).HasColumnName("error").HasMaxLength(2000);
            e.Property(x => x.IsRedelivery).HasColumnName("is_redelivery").HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.Attempt).HasColumnName("attempt").HasDefaultValue(1).ValueGeneratedNever();
            e.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            e.Property(x => x.DeliveredAt).HasColumnName("delivered_at");
            e.HasOne(x => x.Subscription).WithMany().HasForeignKey(x => x.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.SubscriptionId, x.CreatedAt }).HasDatabaseName("ix_webhook_deliveries_subscription_created");
            e.HasIndex(x => x.NextAttemptAt).HasDatabaseName("ix_webhook_deliveries_retry").HasFilter("next_attempt_at is not null");
        });
    }

    /// <summary>
    /// SQL tạo bảng partition <c>ticket_events</c> (BR-SCALE-02). Dùng trong migration và trong
    /// test tạo DB trống. PK/unique đều chứa <c>created_at</c> theo ràng buộc của PostgreSQL.
    /// </summary>
    public const string TicketEventsPartitionSql = """
        create sequence if not exists ticket_events_sequence_seq as bigint start 1 increment 1;

        create table if not exists ticket_events (
            sequence        bigint       not null default nextval('ticket_events_sequence_seq'),
            id              uuid         not null,
            ticket_id       uuid         not null references tickets(id) on delete cascade,
            ticket_version  integer      not null,
            actor_id        uuid         null references users(id) on delete set null,
            event_type      varchar(50)  not null,
            payload         jsonb        not null default '{}'::jsonb,
            visibility      varchar(16)  not null default 'PUBLIC',
            correlation_id  uuid         null,
            created_at      timestamptz  not null default now(),
            constraint pk_ticket_events primary key (sequence, created_at),
            constraint ux_ticket_events_ticket_version unique (ticket_id, ticket_version, created_at),
            constraint ux_ticket_events_id unique (id, created_at),
            constraint ck_ticket_events_visibility check (visibility in ('PUBLIC','INTERNAL','ACTOR_ONLY'))
        ) partition by range (created_at);

        create index if not exists ix_ticket_events_ticket_sequence on ticket_events (ticket_id, sequence);
        create index if not exists ix_ticket_events_type_created on ticket_events (event_type, created_at);
        create index if not exists ix_ticket_events_actor on ticket_events (actor_id) where actor_id is not null;

        create table if not exists ticket_events_default partition of ticket_events default;

        create or replace function ticket_events_ensure_partition(month_start date)
        returns void language plpgsql as $$
        declare
            part_name text := format('ticket_events_y%sm%s', to_char(month_start, 'YYYY'), to_char(month_start, 'MM'));
            range_from date := date_trunc('month', month_start)::date;
            range_to date := (date_trunc('month', month_start) + interval '1 month')::date;
        begin
            if not exists (select 1 from pg_class where relname = part_name) then
                execute format('create table %I partition of ticket_events for values from (%L) to (%L)',
                    part_name, range_from, range_to);
            end if;
        end $$;

        select ticket_events_ensure_partition(date_trunc('month', now())::date);
        select ticket_events_ensure_partition((date_trunc('month', now()) + interval '1 month')::date);
        """;
}
