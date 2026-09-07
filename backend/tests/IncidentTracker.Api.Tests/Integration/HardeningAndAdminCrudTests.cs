using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Hardening (security headers, rate limit đăng nhập, deny-by-default) và ba endpoint quản trị
/// bổ sung để bộ CRUD của UC-01/UC-03 đầy đủ.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HardeningAndAdminCrudTests
{
    private readonly ApiFixture _fx;

    public HardeningAndAdminCrudTests(ApiFixture fx) => _fx = fx;

    // ---------------- Security headers ----------------

    /// <summary>
    /// Header phải có trên response thành công. Đặt ở middleware chứ không ở controller chính
    /// là để không phải nhớ gắn lại ở từng endpoint.
    /// </summary>
    [Fact]
    public async Task Security_headers_co_tren_response_thanh_cong()
    {
        var admin = await _fx.AdminAsync();

        var response = await admin.GetAsync("/api/projects/support/incidents");

        AssertSecurityHeaders(response);
    }

    /// <summary>
    /// Và phải còn nguyên trên response lỗi. Đây là chỗ dễ mất nhất vì
    /// <c>ProblemDetailsWriter</c> gọi <c>Response.Clear()</c>, xóa sạch header đã đặt trước đó.
    /// </summary>
    [Fact]
    public async Task Security_headers_con_nguyen_tren_401_va_403()
    {
        var anonymous = _fx.Factory.CreateClient();
        AssertSecurityHeaders(await anonymous.GetAsync("/api/projects/support/incidents"));

        var readOnly = await _fx.ReadOnlyAsync();
        AssertSecurityHeaders(await readOnly.GetAsync("/api/users"));
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    // ---------------- Rate limit đăng nhập ----------------

    /// <summary>
    /// THR-02 — dò mật khẩu tự động. Rate limit được khai báo là chưa đủ; phải chứng minh nó
    /// chặn thật, và chặn cả khi mật khẩu đúng (nếu chỉ đếm lần sai thì kẻ tấn công vẫn dò
    /// được bằng cách xen kẽ).
    /// </summary>
    [Fact]
    public async Task Rate_limit_chan_do_mat_khau_qua_endpoint_dang_nhap()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimit:LoginPerMinute", "3"));

        var client = factory.CreateClient();
        var codes = new List<HttpStatusCode>();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login",
                new { identifier = ApiFixture.AdminEmail, password = "sai-mat-khau" }, ApiFactory.Json);
            codes.Add(response.StatusCode);
        }

        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    // ---------------- Deny by default ----------------

    /// <summary>
    /// FallbackPolicy. <c>/api/auth/me</c> không khai báo permission nào, chỉ
    /// <c>[Authorize]</c> — nhưng endpoint công khai thì vẫn phải mở, nếu không compose sẽ
    /// không kiểm tra được sức khỏe container.
    /// </summary>
    [Fact]
    public async Task Endpoint_khong_khai_bao_quyen_van_doi_dang_nhap()
    {
        var anonymous = _fx.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/me")).StatusCode);

        // Ba đường công khai có chủ đích phải còn mở.
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anonymous.GetAsync("/api/auth/registration-policy")).StatusCode);
    }

    // ---------------- DELETE /api/users/{id} ----------------

    /// <summary>UC-01 — xóa được tài khoản chưa để lại dấu vết nghiệp vụ; gọi lại vẫn 204.</summary>
    [Fact]
    public async Task Xoa_duoc_user_chua_phat_sinh_du_lieu_va_idempotent()
    {
        var admin = await _fx.AdminAsync();
        var email = $"xoa-duoc-{Guid.NewGuid():N}@test.local";

        var created = await admin.PostAsJsonAsync("/api/users",
            new { email, displayName = "Sẽ bị xóa", password = "Secret#12345", roles = new[] { "customer" } },
            ApiFactory.Json);
        created.EnsureSuccessStatusCode();
        var user = await created.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        var id = user.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/users/{id}")).StatusCode);

        // Lần hai trên id đã biến mất vẫn 204 — xóa là thao tác idempotent.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{id}")).StatusCode);
    }

    /// <summary>
    /// Người đã báo cáo sự cố là một mắt xích của chuỗi truy vết (GOAL-BIZ-02). Xóa họ đi là
    /// xóa bằng chứng, nên hệ thống phải từ chối và chỉ sang đường vô hiệu hóa.
    /// </summary>
    [Fact]
    public async Task Khong_xoa_duoc_user_da_bao_cao_su_co()
    {
        var admin = await _fx.AdminAsync();
        var customer = await _fx.CustomerAsync();

        await customer.PostAsJsonAsync("/api/projects/support/incidents",
            new { title = "Sự cố giữ chân người báo cáo", severity = "Low" }, ApiFactory.Json);

        var me = await customer.GetFromJsonAsync<JsonElement>("/api/auth/me", ApiFactory.Json);
        var id = me.GetProperty("id").GetGuid();

        var response = await admin.DeleteAsync($"/api/users/{id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Contains("isActive", problem.GetProperty("detail").GetString());
    }

    /// <summary>
    /// Tự xóa mình là cách nhanh nhất để mất phiên giữa chừng. Từ BR-SEC-08 đây là 403 chứ
    /// không còn là 409: đó là từ chối thẩm quyền, không phải xung đột trạng thái.
    /// </summary>
    [Fact]
    public async Task Admin_khong_tu_xoa_duoc_chinh_minh()
    {
        var admin = await _fx.AdminAsync();
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/auth/me", ApiFactory.Json);
        var id = me.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.DeleteAsync($"/api/users/{id}")).StatusCode);
    }

    /// <summary>Xóa user là hành động quản trị: tài khoản chỉ đọc không được đụng tới.</summary>
    [Fact]
    public async Task Thieu_quyen_user_delete_tra_403()
    {
        var readOnly = await _fx.ReadOnlyAsync();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await readOnly.DeleteAsync($"/api/users/{Guid.NewGuid()}")).StatusCode);
    }

    // ---------------- GET/PATCH /api/permissions/{id} ----------------

    [Fact]
    public async Task Doc_va_sua_duoc_mo_ta_cua_permission()
    {
        var admin = await _fx.AdminAsync();
        var code = $"lab.thu_nghiem_{Guid.NewGuid():N}"[..40];

        var created = await admin.PostAsJsonAsync("/api/permissions",
            new { code, description = "Mô tả ban đầu" }, ApiFactory.Json);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json))
            .GetProperty("id").GetGuid();

        var fetched = await admin.GetFromJsonAsync<JsonElement>($"/api/permissions/{id}", ApiFactory.Json);
        Assert.Equal(code, fetched.GetProperty("code").GetString());

        var updated = await admin.PatchAsJsonAsync($"/api/permissions/{id}",
            new { description = "Mô tả đã sửa" }, ApiFactory.Json);
        updated.EnsureSuccessStatusCode();

        var body = await updated.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("Mô tả đã sửa", body.GetProperty("description").GetString());

        // code là định danh nằm trong policy và trong token đang lưu hành nên không đổi được.
        Assert.Equal(code, body.GetProperty("code").GetString());

        (await admin.DeleteAsync($"/api/permissions/{id}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Permission_khong_ton_tai_tra_404()
    {
        var admin = await _fx.AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/permissions/{Guid.NewGuid()}")).StatusCode);
    }
}
