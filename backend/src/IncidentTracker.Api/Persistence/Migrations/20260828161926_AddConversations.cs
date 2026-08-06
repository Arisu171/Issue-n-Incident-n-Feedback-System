using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "feedbacks",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "New");

            // Dữ liệu cũ: feedback đã được gắn vào sự cố tức là đã có người tiếp nhận
            // (BR-BIZ-12) — backfill để trạng thái phản ánh đúng lịch sử.
            migrationBuilder.Sql(
                "update feedbacks set status = 'Acknowledged' where incident_id is not null");

            migrationBuilder.CreateTable(
                name: "feedback_replies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    feedback_id = table.Column<Guid>(type: "uuid", nullable: false),
                    responder_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    is_automatic = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feedback_replies", x => x.id);
                    table.CheckConstraint("ck_feedback_replies_author", "is_automatic = true or responder_id is not null");
                    table.CheckConstraint("ck_feedback_replies_body_length", "char_length(body) between 1 and 2000");
                    table.ForeignKey(
                        name: "FK_feedback_replies_feedbacks_feedback_id",
                        column: x => x.feedback_id,
                        principalTable: "feedbacks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_feedback_replies_users_responder_id",
                        column: x => x.responder_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "incident_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_comments", x => x.id);
                    table.CheckConstraint("ck_incident_comments_body_length", "char_length(body) between 1 and 2000");
                    table.ForeignKey(
                        name: "FK_incident_comments_incidents_incident_id",
                        column: x => x.incident_id,
                        principalTable: "incidents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_incident_comments_users_author_id",
                        column: x => x.author_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_feedbacks_status",
                table: "feedbacks",
                sql: "status in ('New','Acknowledged','Responded')");

            migrationBuilder.CreateIndex(
                name: "ix_feedback_replies_feedback_created_at",
                table: "feedback_replies",
                columns: new[] { "feedback_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_feedback_replies_responder_id",
                table: "feedback_replies",
                column: "responder_id");

            migrationBuilder.CreateIndex(
                name: "IX_incident_comments_author_id",
                table: "incident_comments",
                column: "author_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_comments_incident_created_at",
                table: "incident_comments",
                columns: new[] { "incident_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feedback_replies");

            migrationBuilder.DropTable(
                name: "incident_comments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_feedbacks_status",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "status",
                table: "feedbacks");
        }
    }
}
