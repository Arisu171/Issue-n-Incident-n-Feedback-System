using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TicketingV31 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "login",
                table: "users",
                type: "character varying(39)",
                maxLength: 39,
                nullable: false,
                defaultValue: "");

            // Backfill users.login từ phần trước '@' của email (chuẩn hoá về [a-z0-9-]), khử trùng
            // bằng hậu tố số — phải chạy trước unique index và check constraint bên dưới.
            migrationBuilder.Sql("""
                with base as (
                    select id,
                           coalesce(nullif(trim(both '-' from regexp_replace(lower(split_part(email, '@', 1)), '[^a-z0-9]+', '-', 'g')), ''), 'user') as stem,
                           row_number() over (
                               partition by coalesce(nullif(trim(both '-' from regexp_replace(lower(split_part(email, '@', 1)), '[^a-z0-9]+', '-', 'g')), ''), 'user')
                               order by created_at, id) as rn
                    from users
                )
                update users u
                set login = case when b.rn = 1 then left(b.stem, 39)
                                 else left(b.stem, 39 - length('-' || b.rn::text)) || '-' || b.rn::text end
                from base b
                where b.id = u.id and (u.login is null or u.login = '');
                """);

            migrationBuilder.CreateTable(
                name: "boards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    automation = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    is_closed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_boards", x => x.id);
                    table.CheckConstraint("ck_boards_visibility", "visibility in ('PRIVATE','INTERNAL')");
                    table.ForeignKey(
                        name: "FK_boards_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    response_content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    response_headers = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_keys", x => new { x.key, x.user_id });
                });

            migrationBuilder.CreateTable(
                name: "issue_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    position = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issue_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    blank_issues_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    strict_close_policy = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    auto_reopen_on_customer_comment = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    customers_see_only_own = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    next_ticket_number = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    contact_links = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.id);
                    table.CheckConstraint("ck_projects_next_number", "next_ticket_number >= 1");
                    table.CheckConstraint("ck_projects_slug_format", "slug ~ '^[a-z0-9][a-z0-9._-]{0,98}$'");
                });

            migrationBuilder.CreateTable(
                name: "sla_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    priority = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    response_time_minutes = table.Column<int>(type: "integer", nullable: false),
                    resolution_time_minutes = table.Column<int>(type: "integer", nullable: false),
                    escalate_after_minutes = table.Column<int>(type: "integer", nullable: false, defaultValue: 60),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sla_policies", x => x.id);
                    table.CheckConstraint("ck_sla_policies_priority", "priority in ('P0','P1','P2','P3')");
                });

            migrationBuilder.CreateTable(
                name: "board_columns",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    board_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_board_columns", x => x.id);
                    table.ForeignKey(
                        name: "FK_board_columns_boards_board_id",
                        column: x => x.board_id,
                        principalTable: "boards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "labels",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    color_hex = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_labels", x => x.id);
                    table.CheckConstraint("ck_labels_color", "color_hex ~ '^[0-9a-fA-F]{6}$'");
                    table.CheckConstraint("ck_labels_name_length", "char_length(name) between 1 and 50");
                    table.ForeignKey(
                        name: "FK_labels_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "milestones",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    due_on = table.Column<DateOnly>(type: "date", nullable: true),
                    state = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "OPEN"),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    open_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    closed_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_milestones", x => x.id);
                    table.CheckConstraint("ck_milestones_state", "state in ('OPEN','CLOSED')");
                    table.CheckConstraint("ck_milestones_title_length", "char_length(title) between 1 and 200");
                    table.ForeignKey(
                        name: "FK_milestones_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_watches",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_watches", x => new { x.project_id, x.user_id });
                    table.CheckConstraint("ck_project_watches_level", "level in ('ALL','PARTICIPATING','IGNORE','CUSTOM')");
                    table.ForeignKey(
                        name: "FK_project_watches_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_watches_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_templates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    title_prefix = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    defaults = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    body_schema = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    position = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_templates", x => x.id);
                    table.ForeignKey(
                        name: "FK_ticket_templates_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    secret_hmac = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    events = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_subscriptions", x => x.id);
                    table.CheckConstraint("ck_webhook_subscriptions_url", "target_url ~* '^https?://'");
                    table.ForeignKey(
                        name: "FK_webhook_subscriptions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_webhook_subscriptions_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "OPEN"),
                    state_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    duplicate_of_ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    active_lock_reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sub_issue_position = table.Column<int>(type: "integer", nullable: true),
                    milestone_id = table.Column<Guid>(type: "uuid", nullable: true),
                    priority = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    sla_due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_response_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    comments_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    reactions_summary = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tickets", x => x.id);
                    table.CheckConstraint("ck_tickets_closed_pairing", "(state = 'CLOSED' and closed_at is not null) or (state = 'OPEN' and closed_at is null)");
                    table.CheckConstraint("ck_tickets_duplicate_pairing", "state_reason <> 'DUPLICATE' or duplicate_of_ticket_id is not null");
                    table.CheckConstraint("ck_tickets_lock_reason", "active_lock_reason is null or active_lock_reason in ('OFF_TOPIC','TOO_HEATED','RESOLVED','SPAM')");
                    table.CheckConstraint("ck_tickets_number_positive", "number >= 1");
                    table.CheckConstraint("ck_tickets_priority", "priority is null or priority in ('P0','P1','P2','P3')");
                    table.CheckConstraint("ck_tickets_state", "state in ('OPEN','CLOSED')");
                    table.CheckConstraint("ck_tickets_state_reason", "state_reason is null or state_reason in ('COMPLETED','NOT_PLANNED','DUPLICATE','REOPENED')");
                    table.CheckConstraint("ck_tickets_title_length", "char_length(title) between 1 and 256");
                    table.ForeignKey(
                        name: "FK_tickets_issue_types_type_id",
                        column: x => x.type_id,
                        principalTable: "issue_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tickets_milestones_milestone_id",
                        column: x => x.milestone_id,
                        principalTable: "milestones",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tickets_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tickets_tickets_parent_ticket_id",
                        column: x => x.parent_ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_tickets_users_author_id",
                        column: x => x.author_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    request_body = table.Column<string>(type: "text", nullable: false),
                    request_headers = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    http_status = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    is_redelivery = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    attempt = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "FK_webhook_deliveries_webhook_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalTable: "webhook_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "board_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    board_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: true),
                    draft_title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    column_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_board_items", x => x.id);
                    table.CheckConstraint("ck_board_items_kind", "ticket_id is not null or draft_title is not null");
                    table.ForeignKey(
                        name: "FK_board_items_board_columns_column_id",
                        column: x => x.column_id,
                        principalTable: "board_columns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_board_items_boards_board_id",
                        column: x => x.board_id,
                        principalTable: "boards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_board_items_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    unread = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    is_done = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    is_saved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    last_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_event_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    last_actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_threads", x => x.id);
                    table.CheckConstraint("ck_notification_threads_reason", "reason in ('ASSIGN','AUTHOR','COMMENT','MANUAL','MENTION','STATE_CHANGE','SUBSCRIBED','TEAM_MENTION','SLA_BREACH')");
                    table.ForeignKey(
                        name: "FK_notification_threads_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_threads_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_assignees",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_assignees", x => new { x.ticket_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_ticket_assignees_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ticket_assignees_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_labels",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_labels", x => new { x.ticket_id, x.label_id });
                    table.ForeignKey(
                        name: "FK_ticket_labels_labels_label_id",
                        column: x => x.label_id,
                        principalTable: "labels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ticket_labels_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_reactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reaction_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_reactions", x => x.id);
                    table.CheckConstraint("ck_ticket_reactions_type", "reaction_type in ('THUMBS_UP','THUMBS_DOWN','LAUGH','HOORAY','CONFUSED','HEART','ROCKET','EYES')");
                    table.ForeignKey(
                        name: "FK_ticket_reactions_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ticket_reactions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_references",
                columns: table => new
                {
                    source_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    relation_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_references", x => new { x.source_id, x.target_id, x.relation_type });
                    table.CheckConstraint("ck_ticket_references_not_self", "source_id <> target_id");
                    table.CheckConstraint("ck_ticket_references_type", "relation_type in ('BLOCKED_BY','CROSS_REFERENCE','DUPLICATE_OF')");
                    table.ForeignKey(
                        name: "FK_ticket_references_tickets_source_id",
                        column: x => x.source_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ticket_references_tickets_target_id",
                        column: x => x.target_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ticket_subscriptions",
                columns: table => new
                {
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_subscriptions", x => new { x.ticket_id, x.user_id });
                    table.CheckConstraint("ck_ticket_subscriptions_reason", "reason in ('AUTHOR','ASSIGNED','COMMENTED','MENTIONED','MANUAL','STATE_CHANGE','TEAM_MENTION')");
                    table.CheckConstraint("ck_ticket_subscriptions_state", "state in ('SUBSCRIBED','UNSUBSCRIBED','IGNORED')");
                    table.ForeignKey(
                        name: "FK_ticket_subscriptions_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ticket_subscriptions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_users_login",
                table: "users",
                column: "login",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_login_format",
                table: "users",
                sql: "login ~ '^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,38}$'");

            migrationBuilder.CreateIndex(
                name: "ux_board_columns_board_name",
                table: "board_columns",
                columns: new[] { "board_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_board_items_column_id",
                table: "board_items",
                column: "column_id");

            migrationBuilder.CreateIndex(
                name: "ix_board_items_column_position",
                table: "board_items",
                columns: new[] { "board_id", "column_id", "position" });

            migrationBuilder.CreateIndex(
                name: "IX_board_items_ticket_id",
                table: "board_items",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ux_board_items_board_ticket",
                table: "board_items",
                columns: new[] { "board_id", "ticket_id" },
                unique: true,
                filter: "ticket_id is not null");

            migrationBuilder.CreateIndex(
                name: "IX_boards_created_by_id",
                table: "boards",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_expires",
                table: "idempotency_keys",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_issue_types_name",
                table: "issue_types",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_labels_project_lower_name",
                table: "labels",
                columns: new[] { "project_id", "name" },
                unique: true,
                filter: "is_archived = false");

            migrationBuilder.CreateIndex(
                name: "ux_milestones_project_number",
                table: "milestones",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_milestones_project_title",
                table: "milestones",
                columns: new[] { "project_id", "title" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_threads_inbox",
                table: "notification_threads",
                columns: new[] { "user_id", "unread", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_threads_ticket_id",
                table: "notification_threads",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ux_notification_threads_user_ticket",
                table: "notification_threads",
                columns: new[] { "user_id", "ticket_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_watches_user_id",
                table: "project_watches",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_projects_slug",
                table: "projects",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sla_policies_priority",
                table: "sla_policies",
                column: "priority",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_assignees_user",
                table: "ticket_assignees",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_labels_label",
                table: "ticket_labels",
                column: "label_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_reactions_event",
                table: "ticket_reactions",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "IX_ticket_reactions_user_id",
                table: "ticket_reactions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_ticket_reactions_target_user_type",
                table: "ticket_reactions",
                columns: new[] { "ticket_id", "event_id", "user_id", "reaction_type" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_ticket_references_target",
                table: "ticket_references",
                columns: new[] { "target_id", "relation_type" });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_subscriptions_user",
                table: "ticket_subscriptions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_ticket_templates_project_name",
                table: "ticket_templates",
                columns: new[] { "project_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_author",
                table: "tickets",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_milestone",
                table: "tickets",
                column: "milestone_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_parent",
                table: "tickets",
                column: "parent_ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_pinned",
                table: "tickets",
                column: "project_id",
                filter: "is_pinned = true");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_project_state_updated",
                table: "tickets",
                columns: new[] { "project_id", "state", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_sla_due",
                table: "tickets",
                column: "sla_due_at",
                filter: "state = 'OPEN' and sla_due_at is not null");

            migrationBuilder.CreateIndex(
                name: "IX_tickets_type_id",
                table: "tickets",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "ux_tickets_project_number",
                table: "tickets",
                columns: new[] { "project_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_retry",
                table: "webhook_deliveries",
                column: "next_attempt_at",
                filter: "next_attempt_at is not null");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_deliveries_subscription_created",
                table: "webhook_deliveries",
                columns: new[] { "subscription_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_created_by_id",
                table: "webhook_subscriptions",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_project_id",
                table: "webhook_subscriptions",
                column: "project_id");

            // BR-SCALE-02 — Event Store partition theo tháng, PK/unique chứa created_at; sequence
            // tường minh vì PG16 chưa hỗ trợ identity trên bảng partition. Bảng này được đánh dấu
            // ExcludeFromMigrations trong model nên phải tạo bằng SQL tại đây.
            migrationBuilder.Sql(AppDbContext.TicketEventsPartitionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                drop function if exists ticket_events_ensure_partition(date);
                drop table if exists ticket_events cascade;
                drop sequence if exists ticket_events_sequence_seq;
                """);

            migrationBuilder.DropTable(
                name: "board_items");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "notification_threads");

            migrationBuilder.DropTable(
                name: "project_watches");

            migrationBuilder.DropTable(
                name: "sla_policies");

            migrationBuilder.DropTable(
                name: "ticket_assignees");

            migrationBuilder.DropTable(
                name: "ticket_labels");

            migrationBuilder.DropTable(
                name: "ticket_reactions");

            migrationBuilder.DropTable(
                name: "ticket_references");

            migrationBuilder.DropTable(
                name: "ticket_subscriptions");

            migrationBuilder.DropTable(
                name: "ticket_templates");

            migrationBuilder.DropTable(
                name: "webhook_deliveries");

            migrationBuilder.DropTable(
                name: "board_columns");

            migrationBuilder.DropTable(
                name: "labels");

            migrationBuilder.DropTable(
                name: "tickets");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions");

            migrationBuilder.DropTable(
                name: "boards");

            migrationBuilder.DropTable(
                name: "issue_types");

            migrationBuilder.DropTable(
                name: "milestones");

            migrationBuilder.DropTable(
                name: "projects");

            migrationBuilder.DropIndex(
                name: "ux_users_login",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_login_format",
                table: "users");

            migrationBuilder.DropColumn(
                name: "login",
                table: "users");
        }
    }
}
