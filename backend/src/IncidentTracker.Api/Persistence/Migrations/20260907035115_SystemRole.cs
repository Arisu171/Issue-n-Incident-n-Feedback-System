using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IncidentTracker.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SystemRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Không đổi lược đồ — đây là bước **di trú dữ liệu phân quyền**.
            //
            // Vai trò dựng sẵn được seed lúc khởi động, nhưng bước seed đó chỉ **thêm**, không bao
            // giờ gỡ: bỏ một permission khỏi `DefaultRoles` trong mã sẽ không gỡ nó khỏi cơ sở dữ
            // liệu đang chạy. Nên việc chuyển hai quyền cấu hình nền từ `admin` sang `system`
            // phải làm ở đây, và phải chạy đúng một lần.
            //
            // Vai trò `system` được tạo luôn tại đây thay vì chờ bước seed: lệnh cấp quyền bên
            // dưới cần hàng đó tồn tại ngay. Bước seed chạy sau sẽ nhận ra nó theo tên và chỉ cập
            // nhật mô tả cùng danh sách permission.
            migrationBuilder.Sql("""
                insert into roles (name, description, rank, is_global)
                select 'system', 'Cấu hình nền hệ thống — chính sách SLA, loại issue, và mọi quyền của admin', 120, true
                where not exists (select 1 from roles where name = 'system');
                """);

            // Giữ nguyên hiện trạng lúc bật: mọi tài khoản đang mang `admin` nhận thêm `system`,
            // nên không ai mất quyền cấu hình SLA hay loại issue giữa chừng. Thu hẹp lại — bỏ
            // `system` khỏi những admin không cần — là quyết định của con người trên màn hình
            // RBAC, không phải hệ quả âm thầm của một lần chạy migration.
            migrationBuilder.Sql("""
                insert into user_roles (user_id, role_id)
                select ur.user_id, s.id
                from user_roles ur
                join roles a on a.id = ur.role_id and a.name = 'admin'
                cross join roles s
                where s.name = 'system'
                on conflict do nothing;
                """);

            // Và giờ mới gỡ: cấu hình nền rời khỏi `admin`, còn `manager` mất `issue_type.manage`
            // vốn chưa bao giờ dùng được (endpoint loại issue chỉ nhận quyền toàn cục).
            migrationBuilder.Sql("""
                delete from role_permissions rp
                using roles r, permissions p
                where rp.role_id = r.id and rp.permission_id = p.id
                  and r.name = 'admin' and p.code in ('sla.manage', 'issue_type.manage');
                """);

            migrationBuilder.Sql("""
                delete from role_permissions rp
                using roles r, permissions p
                where rp.role_id = r.id and rp.permission_id = p.id
                  and r.name = 'manager' and p.code = 'issue_type.manage';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Trả hai quyền về cho admin. Không xoá vai trò `system`: nếu người vận hành đã cấp
            // nó cho ai đó thì xoá là lấy mất quyền của họ mà không báo.
            migrationBuilder.Sql("""
                insert into role_permissions (role_id, permission_id)
                select r.id, p.id
                from roles r, permissions p
                where r.name = 'admin' and p.code in ('sla.manage', 'issue_type.manage')
                on conflict do nothing;
                """);
        }
    }
}
