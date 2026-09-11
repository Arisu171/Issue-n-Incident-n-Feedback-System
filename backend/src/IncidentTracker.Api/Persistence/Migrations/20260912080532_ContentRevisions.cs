using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContentRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_edited_at",
                table: "incidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_edited_by",
                table: "incidents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_edited_at",
                table: "incident_comments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_edited_by",
                table: "incident_comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_edited_at",
                table: "feedbacks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_edited_by",
                table: "feedbacks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_edited_at",
                table: "feedback_replies",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_edited_by",
                table: "feedback_replies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "content_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    entity_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    old_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: true),
                    edited_by = table.Column<Guid>(type: "uuid", nullable: false),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    on_behalf = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_revisions", x => x.id);
                    table.CheckConstraint("ck_content_revisions_changed", "old_value is distinct from new_value");
                    table.CheckConstraint("ck_content_revisions_entity_type", "entity_type in ('Incident','IncidentComment','Feedback','FeedbackReply')");
                    table.CheckConstraint("ck_content_revisions_field_format", "field ~ '^[a-z][a-z0-9_]*$'");
                    table.CheckConstraint("ck_content_revisions_reason_length", "reason is null or char_length(reason) between 1 and 500");
                    table.ForeignKey(
                        name: "FK_content_revisions_users_edited_by",
                        column: x => x.edited_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_last_edited_by",
                table: "incidents",
                column: "last_edited_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incidents_last_edit_pairing",
                table: "incidents",
                sql: "(last_edited_at is null) = (last_edited_by is null)");

            migrationBuilder.CreateIndex(
                name: "IX_incident_comments_last_edited_by",
                table: "incident_comments",
                column: "last_edited_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_incident_comments_last_edit_pairing",
                table: "incident_comments",
                sql: "(last_edited_at is null) = (last_edited_by is null)");

            migrationBuilder.CreateIndex(
                name: "IX_feedbacks_last_edited_by",
                table: "feedbacks",
                column: "last_edited_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_feedbacks_last_edit_pairing",
                table: "feedbacks",
                sql: "(last_edited_at is null) = (last_edited_by is null)");

            migrationBuilder.CreateIndex(
                name: "IX_feedback_replies_last_edited_by",
                table: "feedback_replies",
                column: "last_edited_by");

            migrationBuilder.AddCheckConstraint(
                name: "ck_feedback_replies_automatic_immutable",
                table: "feedback_replies",
                sql: "is_automatic = false or last_edited_at is null");

            migrationBuilder.AddCheckConstraint(
                name: "ck_feedback_replies_last_edit_pairing",
                table: "feedback_replies",
                sql: "(last_edited_at is null) = (last_edited_by is null)");

            migrationBuilder.CreateIndex(
                name: "IX_content_revisions_edited_by",
                table: "content_revisions",
                column: "edited_by");

            migrationBuilder.CreateIndex(
                name: "ix_content_revisions_entity",
                table: "content_revisions",
                columns: new[] { "entity_type", "entity_id", "edited_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_feedback_replies_users_last_edited_by",
                table: "feedback_replies",
                column: "last_edited_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_feedbacks_users_last_edited_by",
                table: "feedbacks",
                column: "last_edited_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_incident_comments_users_last_edited_by",
                table: "incident_comments",
                column: "last_edited_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_incidents_users_last_edited_by",
                table: "incidents",
                column: "last_edited_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_feedback_replies_users_last_edited_by",
                table: "feedback_replies");

            migrationBuilder.DropForeignKey(
                name: "FK_feedbacks_users_last_edited_by",
                table: "feedbacks");

            migrationBuilder.DropForeignKey(
                name: "FK_incident_comments_users_last_edited_by",
                table: "incident_comments");

            migrationBuilder.DropForeignKey(
                name: "FK_incidents_users_last_edited_by",
                table: "incidents");

            migrationBuilder.DropTable(
                name: "content_revisions");

            migrationBuilder.DropIndex(
                name: "IX_incidents_last_edited_by",
                table: "incidents");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incidents_last_edit_pairing",
                table: "incidents");

            migrationBuilder.DropIndex(
                name: "IX_incident_comments_last_edited_by",
                table: "incident_comments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_incident_comments_last_edit_pairing",
                table: "incident_comments");

            migrationBuilder.DropIndex(
                name: "IX_feedbacks_last_edited_by",
                table: "feedbacks");

            migrationBuilder.DropCheckConstraint(
                name: "ck_feedbacks_last_edit_pairing",
                table: "feedbacks");

            migrationBuilder.DropIndex(
                name: "IX_feedback_replies_last_edited_by",
                table: "feedback_replies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_feedback_replies_automatic_immutable",
                table: "feedback_replies");

            migrationBuilder.DropCheckConstraint(
                name: "ck_feedback_replies_last_edit_pairing",
                table: "feedback_replies");

            migrationBuilder.DropColumn(
                name: "last_edited_at",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "last_edited_by",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "last_edited_at",
                table: "incident_comments");

            migrationBuilder.DropColumn(
                name: "last_edited_by",
                table: "incident_comments");

            migrationBuilder.DropColumn(
                name: "last_edited_at",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "last_edited_by",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "last_edited_at",
                table: "feedback_replies");

            migrationBuilder.DropColumn(
                name: "last_edited_by",
                table: "feedback_replies");
        }
    }
}
