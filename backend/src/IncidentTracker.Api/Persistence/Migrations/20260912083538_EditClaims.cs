using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EditClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "incidents",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "incident_comments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "feedbacks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "feedback_replies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "edit_claims",
                columns: table => new
                {
                    entity_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_edit_claims", x => new { x.entity_type, x.entity_id, x.user_id });
                    table.CheckConstraint("ck_edit_claims_entity_type", "entity_type in ('Incident','IncidentComment','Feedback','FeedbackReply')");
                    table.CheckConstraint("ck_edit_claims_expiry", "expires_at > claimed_at");
                    table.ForeignKey(
                        name: "FK_edit_claims_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_edit_claims_expires_at",
                table: "edit_claims",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_edit_claims_user_id",
                table: "edit_claims",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "edit_claims");

            migrationBuilder.DropColumn(
                name: "version",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "version",
                table: "incident_comments");

            migrationBuilder.DropColumn(
                name: "version",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "version",
                table: "feedback_replies");
        }
    }
}
