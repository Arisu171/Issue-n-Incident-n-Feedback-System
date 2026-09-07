using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// BR-SEC-08 — thứ bậc phân quyền. Trước khi có thang cấp, bất kỳ ai giữ
/// <c>user.role.assign</c> đều tự nâng mình lên admin bằng đúng một request, và hai admin có
/// thể gỡ role hoặc vô hiệu hoá lẫn nhau. Bộ test này khoá lại từng đường đó.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RoleHierarchyTests
{
    private readonly ApiFixture _fx;

    public RoleHierarchyTests(ApiFixture fx) => _fx = fx;

    private static async Task<Guid> MeIdAsync(HttpClient client)
        => (await client.GetFromJsonAsync<JsonElement>("/api/auth/me", ApiFactory.Json))
            .GetProperty("id").GetGuid();

    private static async Task<Guid> RoleIdAsync(HttpClient admin, string name)
    {
        var roles = await admin.GetFromJsonAsync<List<ApiFixture.RoleDto>>("/api/roles", ApiFactory.Json);
        return roles!.Single(r => r.Name == name).Id;
    }

    private static async Task<Guid> CreateUserAsync(HttpClient admin, string prefix, params string[] roles)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}"[..24] + "@test.local";
        var response = await admin.PostAsJsonAsync("/api/users",
            new { email, displayName = prefix, password = ApiFixture.Password, roles }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json)).GetProperty("id").GetGuid();
    }

    // ---------------- Tự phục vụ ----------------

    [Fact]
    public async Task Khong_tu_gan_them_role_cho_chinh_minh()
    {
        var admin = await _fx.AdminAsync();
        var me = await MeIdAsync(admin);
        var adminRole = await RoleIdAsync(admin, "admin");

        var response = await admin.PutAsync($"/api/users/{me}/roles/{adminRole}", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Contains("chính mình", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Khong_tu_go_role_cua_chinh_minh()
    {
        var admin = await _fx.AdminAsync();
        var me = await MeIdAsync(admin);
        var adminRole = await RoleIdAsync(admin, "admin");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.DeleteAsync($"/api/users/{me}/roles/{adminRole}")).StatusCode);
    }

    /// <summary>Tự đặt <c>isActive=false</c> là tự khoá cửa từ bên trong.</summary>
    [Fact]
    public async Task Khong_tu_vo_hieu_hoa_tai_khoan_cua_chinh_minh()
    {
        var admin = await _fx.AdminAsync();
        var me = await MeIdAsync(admin);

        var response = await admin.PatchAsJsonAsync($"/api/users/{me}", new { isActive = false }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------- Người ngang cấp ----------------

    /// <summary>
    /// Kết nạp admin mới thì được (cấp ngang), nhưng từ lúc đó hai bên là đồng cấp nên
    /// không ai gỡ role hay khoá tài khoản của ai được nữa.
    ///
    /// Phép thử phải chạy giữa <b>hai admin thường</b>. Tài khoản bootstrap mang thêm vai trò
    /// `system` (cấp 120) nên nó đứng trên admin — dùng nó làm bên chủ động thì đang kiểm quan hệ
    /// trên–dưới chứ không phải quan hệ đồng cấp.
    /// </summary>
    [Fact]
    public async Task Admin_khong_tac_dong_duoc_len_admin_khac()
    {
        // Chỉ `system` (level 120) mới cấp nổi role `admin` (rank 100); admin không tự nhân bản.
        var bootstrap = await _fx.SystemAsync();
        var adminRole = await RoleIdAsync(bootstrap, "admin");

        var actorEmail = $"admin-a-{Guid.NewGuid():N}"[..24] + "@test.local";
        (await bootstrap.PostAsJsonAsync("/api/users",
            new { email = actorEmail, displayName = "Admin A", password = ApiFixture.Password, roles = new[] { "admin" } },
            ApiFactory.Json)).EnsureSuccessStatusCode();
        var admin = await _fx.Factory.CreateClientAsAsync(actorEmail, ApiFixture.Password);

        var peer = await CreateUserAsync(bootstrap, "peer", "admin");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.DeleteAsync($"/api/users/{peer}/roles/{adminRole}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.PatchAsJsonAsync($"/api/users/{peer}", new { isActive = false }, ApiFactory.Json)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await admin.DeleteAsync($"/api/users/{peer}")).StatusCode);

        // Còn tài khoản bootstrap thì đứng trên cả hai, nên nó vẫn gỡ được — đây mới là điều
        // vai trò `system` sinh ra để làm.
        Assert.Equal(HttpStatusCode.NoContent,
            (await bootstrap.DeleteAsync($"/api/users/{peer}/roles/{adminRole}")).StatusCode);
    }

    /// <summary>
    /// Tên vai trò đã bỏ là tên dành riêng.
    ///
    /// Bước gỡ lúc khởi động tìm theo **tên**, nên nếu tạo lại được `viewer` thì lần khởi động kế
    /// tiếp sẽ xoá nó và dồn người dùng sang `customer` — mất một vai trò do người thật tạo, mà
    /// không ai làm gì sai. Chặn ngay lúc tạo là cách duy nhất không phụ thuộc thứ tự khởi động.
    /// </summary>
    [Fact]
    public async Task Khong_tao_lai_duoc_vai_tro_da_bo()
    {
        var admin = await _fx.AdminAsync();

        var response = await admin.PostAsJsonAsync("/api/roles",
            new { name = "viewer", description = "thử tạo lại", rank = 10 }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Contains("customer", problem.GetProperty("detail").GetString()!);

        // Hoa/thường không giúp lách được.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/roles",
            new { name = "Viewer", description = "thử lại", rank = 10 }, ApiFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task Admin_van_tac_dong_duoc_len_cap_thap_hon()
    {
        var admin = await _fx.AdminAsync();
        var supportRole = await RoleIdAsync(admin, "support");
        var target = await CreateUserAsync(admin, "low", "customer");

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsync($"/api/users/{target}/roles/{supportRole}", null)).StatusCode);

        var patched = await admin.PatchAsJsonAsync($"/api/users/{target}", new { isActive = false }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
    }

    // ---------------- Leo thang từ cấp dưới ----------------

    /// <summary>
    /// Kịch bản nguy hiểm nhất: cấp <c>user.role.assign</c> cho một vai trò cấp thấp. Trước
    /// khi có thang cấp, người mang vai trò đó tự cấp <c>admin</c> cho mình là xong.
    /// </summary>
    [Fact]
    public async Task Cap_duoi_giu_quyen_gan_role_van_khong_len_duoc_admin()
    {
        var admin = await _fx.AdminAsync();

        var roleName = $"onboarder-{Guid.NewGuid():N}"[..20];
        var created = await admin.PostAsJsonAsync("/api/roles",
            new { name = roleName, description = "Cấp thấp nhưng được gán role", rank = 40 }, ApiFactory.Json);
        created.EnsureSuccessStatusCode();
        var role = await created.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        var roleId = role.GetProperty("id").GetGuid();
        Assert.Equal(40, role.GetProperty("rank").GetInt32());

        var permissions = await admin.GetFromJsonAsync<List<ApiFixture.PermissionDto>>("/api/permissions", ApiFactory.Json);
        foreach (var code in new[] { "user.read", "user.role.assign" })
        {
            var permission = permissions!.Single(p => p.Code == code);
            (await admin.PutAsync($"/api/roles/{roleId}/permissions/{permission.Id}", null)).EnsureSuccessStatusCode();
        }

        var email = $"onb-{Guid.NewGuid():N}"[..20] + "@test.local";
        (await admin.PostAsJsonAsync("/api/users",
            new { email, displayName = "Onboarder", password = ApiFixture.Password, roles = new[] { roleName } },
            ApiFactory.Json)).EnsureSuccessStatusCode();

        var onboarder = await _fx.Factory.CreateClientAsAsync(email, ApiFixture.Password);
        var self = await MeIdAsync(onboarder);
        var adminRole = await RoleIdAsync(admin, "admin");

        // Lớp chặn thứ nhất, có từ khi quyền gắn theo project: vai trò tự tạo **không toàn cục**
        // nên `user.role.assign` của nó không có hiệu lực ở endpoint quản trị toàn hệ thống. Kẻ
        // muốn leo thang không chạm được tới đường đó.
        var blocked = await onboarder.PutAsync($"/api/users/{self}/roles/{adminRole}", null);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        var blockedProblem = await blocked.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("user.role.assign", blockedProblem.GetProperty("requiredPermission").GetString());

        // Lớp chặn thứ hai — quy tắc cấp bậc (BR-SEC-08) — vẫn phải còn nguyên cho vai trò toàn
        // cục, nên phần dưới đặt vai trò này thành toàn cục rồi chạy lại đúng kịch bản cũ. Ghi
        // thẳng vào bảng vì API cố ý không cho tạo vai trò toàn cục: đó là quyền quá lớn để cấp
        // qua một lượt POST.
        await using (var db = _fx.Factory.CreateDbContext())
        {
            var row = await db.Roles.FirstAsync(r => r.Id == roleId);
            row.IsGlobal = true;
            await db.SaveChangesAsync();
        }

        // Tự nâng mình: chặn vì là chính mình.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await onboarder.PutAsync($"/api/users/{self}/roles/{adminRole}", null)).StatusCode);

        // Nâng người khác lên admin: chặn vì role cao hơn cấp của mình.
        var victim = await CreateUserAsync(admin, "victim", "customer");
        var response = await onboarder.PutAsync($"/api/users/{victim}/roles/{adminRole}", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Contains("cao hơn cấp", problem.GetProperty("detail").GetString());

        // Nhưng vẫn làm được việc đúng thẩm quyền: gán role ngang cấp cho người cấp thấp hơn.
        Assert.Equal(HttpStatusCode.NoContent,
            (await onboarder.PutAsync($"/api/users/{victim}/roles/{roleId}", null)).StatusCode);
    }

    // ---------------- Sửa role của chính mình ----------------

    [Fact]
    public async Task Khong_them_duoc_permission_vao_role_minh_dang_mang()
    {
        var admin = await _fx.AdminAsync();
        var adminRole = await RoleIdAsync(admin, "admin");
        var permissions = await admin.GetFromJsonAsync<List<ApiFixture.PermissionDto>>("/api/permissions", ApiFactory.Json);
        var any = permissions!.First();

        var response = await admin.PutAsync($"/api/roles/{adminRole}/permissions/{any.Id}", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Contains("đang mang role này", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Khong_tao_duoc_role_cao_hon_cap_cua_minh()
    {
        var admin = await _fx.AdminAsync();

        var response = await admin.PostAsJsonAsync("/api/roles",
            new { name = $"superadmin-{Guid.NewGuid():N}"[..20], description = "Vượt admin", rank = 500 },
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------- Cấp hiển thị ra API ----------------

    /// <summary>
    /// Cấp trả ra là cấp **cao nhất** trong các vai trò đang mang. Tài khoản bootstrap mang cả
    /// `admin` (100) lẫn `system` (120) nên nó là 120; một admin thường vẫn là 100.
    /// </summary>
    [Fact]
    public async Task UserResponse_tra_ve_cap_de_giao_dien_an_bot_nut()
    {
        var bootstrap = await _fx.SystemAsync();

        // Cấp 120 thuộc về tài khoản `system`, không phải admin — hai tài khoản tách bạch.
        var me = await bootstrap.GetFromJsonAsync<JsonElement>($"/api/users/{await MeIdAsync(bootstrap)}", ApiFactory.Json);
        Assert.Equal(120, me.GetProperty("level").GetInt32());

        var admin = await _fx.AdminAsync();
        var adminSelf = await admin.GetFromJsonAsync<JsonElement>($"/api/users/{await MeIdAsync(admin)}", ApiFactory.Json);
        Assert.Equal(100, adminSelf.GetProperty("level").GetInt32());

        var plain = await CreateUserAsync(bootstrap, "capadmin", "admin");
        var plainUser = await bootstrap.GetFromJsonAsync<JsonElement>($"/api/users/{plain}", ApiFactory.Json);
        Assert.Equal(100, plainUser.GetProperty("level").GetInt32());
    }
}
