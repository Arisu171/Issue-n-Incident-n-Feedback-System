using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectScopedRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "project_members",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_members", x => new { x.project_id, x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_project_members_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_members_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "project_role_access",
                columns: table => new
                {
                    project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_role_access", x => new { x.project_id, x.role_id });
                    table.ForeignKey(
                        name: "FK_project_role_access_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_role_access_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_project_members_role_id",
                table: "project_members",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_members_user_id",
                table: "project_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_role_access_role_id",
                table: "project_role_access",
                column: "role_id");

            migrationBuilder.AddColumn<bool>(
                name: "is_global",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Chỉ admin là vai trò toàn cục. Mọi vai trò khác từ đây chỉ có quyền ở project được
            // cấp — đó chính là thứ làm cho việc kiểm quyền theo project có hiệu lực thật, thay
            // vì luôn rơi vào nhánh dự phòng toàn cục.
            migrationBuilder.Sql("update roles set is_global = true where name = 'admin';");

            // ---------------------------------------------------------------- di trú dữ liệu
            //
            // Bật kiểm quyền theo project mà không cấp gì là làm mất sạch quyền của 26 tài khoản
            // nhân viên đang chạy. Nên bước này cấp đúng bằng những gì họ đang có: nhân viên
            // thành thành viên của **mọi project hiện có**, vai trò customer với tới **mọi
            // project hiện có**. Không ai mất gì tại thời điểm bật.
            //
            // Thu hẹp lại là việc của con người trên màn hình quản lý thành viên — migration
            // không biết ai nên ở project nào, và đoán thì sai âm thầm.
            //
            // Đặt trong migration chứ không trong seeder vì nó phải chạy **đúng một lần**: seeder
            // chạy mỗi lần khởi động, và cấp lại sau khi chủ dự án vừa thu hẹp là phá hoại.
            migrationBuilder.Sql("""
                insert into project_members (project_id, user_id, role_id, added_at)
                select p.id, ur.user_id, ur.role_id, now()
                from projects p
                cross join user_roles ur
                join roles r on r.id = ur.role_id
                where r.name in ('support', 'responder', 'manager')
                on conflict do nothing;
                """);

            migrationBuilder.Sql("""
                insert into project_role_access (project_id, role_id, added_at)
                select p.id, r.id, now()
                from projects p
                cross join roles r
                where r.name = 'customer'
                on conflict do nothing;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "is_global", table: "roles");

            migrationBuilder.DropTable(
                name: "project_members");

            migrationBuilder.DropTable(
                name: "project_role_access");
        }
    }
}
