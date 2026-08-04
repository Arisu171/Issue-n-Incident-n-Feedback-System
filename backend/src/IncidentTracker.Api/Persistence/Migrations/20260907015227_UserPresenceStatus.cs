using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserPresenceStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "presence_status",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Offline");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "presence_status",
                table: "users");
        }
    }
}
