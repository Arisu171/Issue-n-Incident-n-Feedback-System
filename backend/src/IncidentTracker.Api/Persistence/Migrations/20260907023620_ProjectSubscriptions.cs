using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_subscriptions",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_subscriptions", x => new { x.project_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_project_subscriptions_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_subscriptions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_project_subscriptions_user_id",
                table: "project_subscriptions",
                column: "user_id");

            // Backfill: giữ nguyên hiện trạng lúc bật.
            //
            // Từ bản này, quyền của vai trò tự phục vụ là **giao** của hai vế — project phải mở,
            // và người dùng phải tự nhận. Bảng mới thì rỗng, nên nếu không có dòng dưới đây thì
            // ngay lần khởi động kế tiếp **mọi khách hàng đang dùng đều mất sạch quyền** cho tới
            // khi từng người tự vào tick lại. Thu hẹp phạm vi là việc của người, làm trên tab
            // Projects, không phải hệ quả âm thầm của một lần chạy migration.
            migrationBuilder.Sql(@"
                INSERT INTO project_subscriptions (project_id, user_id, joined_at)
                SELECT DISTINCT a.project_id, ur.user_id, now()
                FROM project_role_access a
                JOIN roles r ON r.id = a.role_id
                JOIN user_roles ur ON ur.role_id = a.role_id
                WHERE lower(r.name) = 'customer'
                ON CONFLICT DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_subscriptions");
        }
    }
}
