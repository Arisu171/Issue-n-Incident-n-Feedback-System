using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoleRankHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rank",
                table: "roles",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddCheckConstraint(
                name: "ck_roles_rank_range",
                table: "roles",
                sql: "\"rank\" between 1 and 1000");

            // Database đã có dữ liệu sẽ nhận default 10 cho MỌI role, tức là admin ngang khách
            // hàng và không ai thao tác được lên ai. Đặt lại đúng thang ngay trong migration để
            // hệ thống dùng được kể cả trước khi seeder chạy.
            migrationBuilder.Sql(@"
                update roles set ""rank"" = 100 where name = 'admin';
                update roles set ""rank"" = 60  where name = 'responder';
                update roles set ""rank"" = 40  where name = 'support';
                update roles set ""rank"" = 10  where name in ('viewer', 'customer');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_roles_rank_range",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "rank",
                table: "roles");
        }
    }
}
