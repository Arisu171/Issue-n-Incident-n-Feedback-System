using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// UC-08 · Tự đăng ký tài khoản.
///
/// Đây là endpoint công khai duy nhất có khả năng ghi vào database, nên phần lớn test ở đây
/// không kiểm chứng đường thành công mà kiểm chứng những gì người đăng ký <b>không</b> làm được.
/// </summary>
[Collection(ApiCollection.Name)]
public class RegistrationTests
{
    private readonly ApiFixture _fx;

    public RegistrationTests(ApiFixture fx) => _fx = fx;

    private static string NewEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.local";

    /// <summary>
    /// Username hợp lệ và duy nhất. Đăng ký giờ đòi người dùng tự chọn username nên mọi payload
    /// đều phải có; cắt còn 20 ký tự để không chạm trần 39 của LoginNames.
    /// </summary>
    private static string NewUsername() => $"u-{Guid.NewGuid():N}"[..20].TrimEnd('-');

    // ---------- Đường thành công ----------

    [Fact]
    public async Task Nguoi_dung_tu_dang_ky_va_dang_nhap_duoc_ngay()
    {
        var client = _fx.Factory.CreateClient();
        var email = NewEmail("dangky");

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email, username = NewUsername(), displayName = "Người tự đăng ký", password = "Secret#12345" },
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

        Assert.False(body.GetProperty("requiresApproval").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("session").ValueKind);

        // Đăng ký xong dùng được ngay, không phải chờ ai.
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = email, password = "Secret#12345" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    /// <summary>Token cấp ngay sau đăng ký phải mang đủ quyền của vai trò mặc định.</summary>
    [Fact]
    public async Task Token_cap_ngay_sau_dang_ky_co_du_quyen_cua_vai_tro_mac_dinh()
    {
        var client = _fx.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = NewEmail("token"), username = NewUsername(), displayName = "Kiểm tra token", password = "Secret#12345" },
            ApiFactory.Json);
        response.EnsureSuccessStatusCode();

        var session = (await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json))
            .GetProperty("session");
        var permissions = session.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetString()).ToList();

        Assert.Contains("incident.read", permissions);

        // Và token dùng được thật, không chỉ đẹp trong response.
        var authed = _fx.Factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers
            .AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());

        // Tài khoản vừa đăng ký chưa dùng dịch vụ của project nào, nên chưa vào được project nào
        // — họ tự chọn ở tab Projects. Vẫn **đúng token đó**: quyền đọc lại từ DB mỗi request nên
        // không phải đăng nhập lại sau khi tự nhận.
        Assert.Equal(HttpStatusCode.Forbidden, (await authed.GetAsync("/api/projects/support/incidents")).StatusCode);
        (await authed.PutAsync("/api/project-catalog/support", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/projects/support/incidents")).StatusCode);
    }

    // ---------- Chống leo thang đặc quyền ----------

    /// <summary>
    /// Quan trọng nhất: người đăng ký gửi kèm roles và isActive thì các trường đó phải bị bỏ
    /// qua hoàn toàn. Nếu không, đường công khai này thành đường tự cấp quyền quản trị.
    /// </summary>
    [Fact]
    public async Task Nguoi_dang_ky_khong_tu_cap_duoc_vai_tro_quan_tri()
    {
        var client = _fx.Factory.CreateClient();
        var email = NewEmail("leothang");

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new
            {
                email,
                username = NewUsername(), displayName = "Kẻ thử leo thang",
                password = "Secret#12345",
                roles = new[] { "admin", "responder" },
                isActive = true,
                permissions = new[] { "user.create" }
            }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        var roles = body.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();

        Assert.DoesNotContain("admin", roles);
        Assert.DoesNotContain("responder", roles);
        Assert.Equal(new[] { "customer" }, roles);

        await using var db = _fx.Factory.CreateDbContext();
        var stored = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .SingleAsync(u => u.Email == email);
        Assert.Equal(new[] { "customer" }, stored.UserRoles.Select(ur => ur.Role.Name).ToArray());
    }

    /// <summary>
    /// UC-BIZ-08 — đường tự đăng ký chính là đường khách hàng vào hệ thống: gửi được sự cố và
    /// phản hồi, nhưng không chạm được vào quản trị và không tự xử lý sự cố của mình.
    /// </summary>
    [Fact]
    public async Task Tai_khoan_tu_dang_ky_gui_duoc_ticket_nhung_khong_quan_tri_duoc()
    {
        var client = _fx.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = NewEmail("khachhang"), username = NewUsername(), displayName = "Khách hàng mới", password = "Secret#12345" },
            ApiFactory.Json);
        response.EnsureSuccessStatusCode();

        var token = (await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json))
            .GetProperty("session").GetProperty("accessToken").GetString();

        var authed = _fx.Factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers
            .AuthenticationHeaderValue("Bearer", token);

        // Tự chọn project mình đang dùng dịch vụ — bước duy nhất giữa "đăng ký xong" và "gửi
        // được sự cố", và cũng là bước không ai làm hộ được.
        (await authed.PutAsync("/api/project-catalog/support", null)).EnsureSuccessStatusCode();

        // Gửi được sự cố ngay sau khi đăng ký — không cần chờ ai tạo tài khoản hộ.
        var created = await authed.PostAsJsonAsync("/api/projects/support/incidents",
            new { title = "Không đăng nhập được vào ứng dụng", severity = "High" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var incident = await created.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        var incidentId = incident.GetProperty("id").GetGuid();
        Assert.Equal("Investigating", incident.GetProperty("status").GetString());

        // Gửi được phản hồi và đọc lại được đúng phần của mình.
        Assert.Equal(HttpStatusCode.Created, (await authed.PostAsJsonAsync("/api/projects/support/feedbacks",
            new { channel = "Web", content = "Ứng dụng báo lỗi 500 khi bấm đăng nhập." },
            ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/projects/support/feedbacks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync($"/api/projects/support/incidents/{incidentId}")).StatusCode);

        // Nhưng không quản trị và không tự xử lý sự cố của mình.
        Assert.Equal(HttpStatusCode.Forbidden, (await authed.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await authed.PatchAsJsonAsync(
            $"/api/projects/support/incidents/{incidentId}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await authed.DeleteAsync($"/api/projects/support/incidents/{incidentId}")).StatusCode);
    }

    /// <summary>
    /// Cấu hình sai vai trò mặc định không được biến thành lỗ hổng: danh sách trắng cứng trong
    /// mã phải lọc bỏ, kể cả khi biến môi trường ghi "admin".
    /// </summary>
    [Fact]
    public async Task Cau_hinh_sai_vai_tro_mac_dinh_bi_danh_sach_trang_chan_lai()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Registration:DefaultRoles", "admin,customer"));

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = NewEmail("cauhinhsai"), username = NewUsername(), displayName = "Cấu hình sai", password = "Secret#12345" },
            ApiFactory.Json);

        response.EnsureSuccessStatusCode();
        var roles = (await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json))
            .GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();

        Assert.DoesNotContain("admin", roles);
        Assert.Contains("customer", roles);
    }

    // ---------- Ràng buộc đầu vào ----------

    [Fact]
    public async Task Email_da_ton_tai_tra_409()
    {
        var client = _fx.Factory.CreateClient();
        var email = NewEmail("trung");

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register",
            new { email, username = NewUsername(), displayName = "Trùng email", password = "Secret#12345" },
            ApiFactory.Json)).StatusCode);

        // Username **khác** để chắc chắn 409 là do email, không phải do username trùng theo.
        var again = await client.PostAsJsonAsync("/api/auth/register",
            new { email, username = NewUsername(), displayName = "Trùng email", password = "Secret#12345" },
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await again.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Contains("Email", problem.GetProperty("detail").GetString()!);
    }

    [Fact]
    public async Task Username_da_ton_tai_tra_409()
    {
        var client = _fx.Factory.CreateClient();
        var username = NewUsername();

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register",
            new { email = NewEmail("un1"), username, displayName = "Người đầu", password = "Secret#12345" },
            ApiFactory.Json)).StatusCode);

        // Email khác, chỉ username trùng.
        var again = await client.PostAsJsonAsync("/api/auth/register",
            new { email = NewEmail("un2"), username, displayName = "Người sau", password = "Secret#12345" },
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await again.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Contains("Username", problem.GetProperty("detail").GetString()!);
    }

    [Theory]
    [InlineData("-mo-dau-gach")]
    [InlineData("ket-thuc-gach-")]
    [InlineData("Có Dấu")]
    [InlineData("có_gạch_dưới")]
    [InlineData("hai--gach")]
    public async Task Username_sai_dinh_dang_tra_400(string username)
    {
        var client = _fx.Factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = NewEmail("sai"), username, displayName = "Sai username", password = "Secret#12345" },
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Đăng nhập được bằng cả email lẫn username — và username sai vẫn phải trả đúng 401 như
    /// email sai, không được lộ tài khoản nào có thật.
    /// </summary>
    [Fact]
    public async Task Dang_nhap_duoc_bang_username_lan_email()
    {
        var client = _fx.Factory.CreateClient();
        var email = NewEmail("hailoi");
        var username = NewUsername();

        (await client.PostAsJsonAsync("/api/auth/register",
            new { email, username, displayName = "Hai lối vào", password = "Secret#12345" },
            ApiFactory.Json)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = email, password = "Secret#12345" }, ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = username, password = "Secret#12345" }, ApiFactory.Json)).StatusCode);
        // Hoa/thường không cản được.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = username.ToUpperInvariant(), password = "Secret#12345" }, ApiFactory.Json)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = username, password = "sai-mat-khau" }, ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = "khong-co-username-nay", password = "Secret#12345" }, ApiFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task Mat_khau_qua_ngan_tra_400()
    {
        var client = _fx.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = NewEmail("matkhau"), username = NewUsername(), displayName = "Mật khẩu ngắn", password = "123" },
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Email_sai_dinh_dang_tra_400()
    {
        var client = _fx.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = "khong-phai-email", username = NewUsername(), displayName = "Sai email", password = "Secret#12345" },
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- Các chế độ cấu hình ----------

    [Fact]
    public async Task Tat_dang_ky_thi_endpoint_tra_403()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Registration:Enabled", "false"));

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = NewEmail("tat"), username = NewUsername(), displayName = "Đăng ký tắt", password = "Secret#12345" },
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Gioi_han_ten_mien_email_chan_dung_nguoi_ngoai_to_chuc()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Registration:AllowedEmailDomains", "cong-ty.vn"));

        var client = factory.CreateClient();

        var outsider = await client.PostAsJsonAsync("/api/auth/register",
            new { email = NewEmail("nguoingoai"), username = NewUsername(), displayName = "Người ngoài", password = "Secret#12345" },
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, outsider.StatusCode);

        var insider = await client.PostAsJsonAsync("/api/auth/register",
            new
            {
                email = $"nhanvien-{Guid.NewGuid():N}@cong-ty.vn",
                username = NewUsername(), displayName = "Nhân viên",
                password = "Secret#12345"
            }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, insider.StatusCode);
    }

    /// <summary>
    /// Chế độ chờ duyệt: tài khoản được tạo nhưng chưa đăng nhập được, và tuyệt đối không được
    /// cấp token — nếu cấp thì lớp duyệt trở nên vô nghĩa.
    /// </summary>
    [Fact]
    public async Task Che_do_cho_duyet_khong_cap_token_va_chua_dang_nhap_duoc()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Registration:RequireApproval", "true"));

        var client = factory.CreateClient();
        var email = NewEmail("choduyet");

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email, username = NewUsername(), displayName = "Chờ duyệt", password = "Secret#12345" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

        Assert.True(body.GetProperty("requiresApproval").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("session").ValueKind);

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = email, password = "Secret#12345" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        // Admin kích hoạt xong thì đăng nhập được — vòng đời khép kín.
        var admin = await _fx.AdminAsync();
        var users = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/users?search={Uri.EscapeDataString(email)}", ApiFactory.Json);
        var userId = users.GetProperty("items")[0].GetProperty("id").GetGuid();

        (await admin.PatchAsJsonAsync($"/api/users/{userId}", new { isActive = true }, ApiFactory.Json))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = email, password = "Secret#12345" }, ApiFactory.Json)).StatusCode);
    }

    /// <summary>
    /// Rate limit là hàng rào chính chống tạo hàng loạt tài khoản rác qua đường công khai,
    /// nên phải kiểm chứng nó chặn thật chứ không chỉ được khai báo.
    /// </summary>
    [Fact]
    public async Task Rate_limit_chan_tao_hang_loat_tai_khoan()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimit:RegisterPerHour", "3"));

        var client = factory.CreateClient();
        var codes = new List<HttpStatusCode>();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/register",
                new { email = NewEmail($"ratelimit{i}"), username = NewUsername(), displayName = $"Rác {i}", password = "Secret#12345" },
                ApiFactory.Json);
            codes.Add(response.StatusCode);
        }

        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Created));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    /// <summary>
    /// Cấu hình dạng chuỗi phân tách dấu phẩy phải bind được từ một biến môi trường duy nhất.
    /// Nếu để kiểu List&lt;string&gt; thì binder đòi khóa đánh số __0, __1 và biến trong .env
    /// sẽ âm thầm không có tác dụng.
    /// </summary>
    [Fact]
    public async Task Nhieu_ten_mien_phan_tach_bang_dau_phay_deu_co_hieu_luc()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Registration:AllowedEmailDomains", " cong-ty.vn , chi-nhanh.vn "));

        var client = factory.CreateClient();

        var policy = await client.GetFromJsonAsync<JsonElement>(
            "/api/auth/registration-policy", ApiFactory.Json);
        var domains = policy.GetProperty("allowedEmailDomains").EnumerateArray()
            .Select(d => d.GetString()).ToList();
        Assert.Equal(new[] { "cong-ty.vn", "chi-nhanh.vn" }, domains);

        foreach (var domain in new[] { "cong-ty.vn", "chi-nhanh.vn" })
        {
            var response = await client.PostAsJsonAsync("/api/auth/register",
                new
                {
                    email = $"nhanvien-{Guid.NewGuid():N}@{domain}",
                    username = NewUsername(), displayName = "Nhân viên",
                    password = "Secret#12345"
                }, ApiFactory.Json);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var outsider = await client.PostAsJsonAsync("/api/auth/register",
            new { email = NewEmail("ngoai"), username = NewUsername(), displayName = "Ngoài", password = "Secret#12345" },
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, outsider.StatusCode);
    }

    // ---------- Chính sách công bố cho giao diện ----------

    [Fact]
    public async Task Giao_dien_doc_duoc_chinh_sach_dang_ky_khi_chua_dang_nhap()
    {
        var client = _fx.Factory.CreateClient();

        var response = await client.GetAsync("/api/auth/registration-policy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policy = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

        Assert.True(policy.GetProperty("enabled").GetBoolean());
        Assert.Equal(8, policy.GetProperty("minPasswordLength").GetInt32());
        Assert.False(policy.GetProperty("requiresApproval").GetBoolean());
    }

    [Fact]
    public async Task Chinh_sach_khong_lo_bi_mat_nao()
    {
        var client = _fx.Factory.CreateClient();
        var raw = await client.GetStringAsync("/api/auth/registration-policy");

        Assert.DoesNotContain(ApiFactory.SigningKey, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", raw, StringComparison.Ordinal);
    }
}
