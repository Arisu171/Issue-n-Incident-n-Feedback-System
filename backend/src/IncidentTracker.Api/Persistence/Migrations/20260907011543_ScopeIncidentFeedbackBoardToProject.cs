using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeIncidentFeedbackBoardToProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "incidents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "feedbacks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "project_id",
                table: "boards",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_incidents_project_id",
                table: "incidents",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_feedbacks_project_id",
                table: "feedbacks",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_boards_project_id",
                table: "boards",
                column: "project_id");

            migrationBuilder.AddForeignKey(
                name: "FK_boards_projects_project_id",
                table: "boards",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_feedbacks_projects_project_id",
                table: "feedbacks",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_incidents_projects_project_id",
                table: "incidents",
                column: "project_id",
                principalTable: "projects",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_boards_projects_project_id",
                table: "boards");

            migrationBuilder.DropForeignKey(
                name: "FK_feedbacks_projects_project_id",
                table: "feedbacks");

            migrationBuilder.DropForeignKey(
                name: "FK_incidents_projects_project_id",
                table: "incidents");

            migrationBuilder.DropIndex(
                name: "ix_incidents_project_id",
                table: "incidents");

            migrationBuilder.DropIndex(
                name: "ix_feedbacks_project_id",
                table: "feedbacks");

            migrationBuilder.DropIndex(
                name: "ix_boards_project_id",
                table: "boards");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "feedbacks");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "boards");
        }
    }
}
