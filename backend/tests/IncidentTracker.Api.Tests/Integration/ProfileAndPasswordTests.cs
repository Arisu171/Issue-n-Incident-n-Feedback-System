using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Hồ sơ người dùng và đổi mật khẩu (đợt 4).
///
/// Hai ranh giới được chốt ở đây:
///
/// 1. <b>Hồ sơ mở cho mọi người đã đăng nhập, nhưng email và số điện thoại thì không.</b> Đường cũ
///    <c>GET /api/users/{id}</c> đòi <c>user.read</c> — chỉ admin — và trả cả danh bạ nội bộ.
/// 2. <b>Đổi mật khẩu giết mọi phiên khác, trừ chính phiên đang đổi.</b> Thiếu vế đầu thì việc đổi
///    vô nghĩa với người nghi bị lộ; thiếu vế sau thì lần nào đổi xong cũng bị đá ra.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProfileAndPasswordTests
{
    private readonly ApiFixture _fx;

    public ProfileAndPasswordTests(ApiFixture fx) => _fx = fx;

    private sealed record Profile(
        Guid Id, string Login, string DisplayName, string? AvatarUrl, string Status, string? Biography,
        string? Email, string? Phone, DateTimeOffset CreatedAt, List<string> Roles,
        bool IsSelf, bool? EmailVisible, bool? PhoneVisible);

    [Fact]
    public async Task Ai_dang_nhap_cung_xem_duoc_ho_so_nhung_email_va_phone_thi_khong()
    {
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();

        // Khách hàng xem hồ sơ nhân viên: mặc định không thấy email lẫn số điện thoại.
        var seen = await customer.GetFromJsonAsync<Profile>("/api/profiles/support", ApiFactory.Json);
        Assert.Equal("support", seen!.Login);
        Assert.Null(seen.Email);
        Assert.Null(seen.Phone);
        Assert.False(seen.IsSelf);
        // Người khác không cần biết ai đang bật gì.
        Assert.Null(seen.EmailVisible);

        // Đường cũ vẫn khoá chặt — mở hồ sơ không có nghĩa là mở danh bạ.
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/users")).StatusCode);

        // Chủ tài khoản luôn thấy của mình, kể cả khi đang tắt.
        var mine = await support.GetFromJsonAsync<Profile>("/api/profiles/support", ApiFactory.Json);
        Assert.True(mine!.IsSelf);
        Assert.Equal(ApiFixture.SupportEmail, mine.Email);
        Assert.False(mine.EmailVisible);

        // Bật lên thì người khác mới thấy.
        (await support.PatchAsJsonAsync("/api/profiles/me",
            new { emailVisible = true, phone = "0900000001", phoneVisible = false, biography = "Trực ca sáng." },
            ApiFactory.Json)).EnsureSuccessStatusCode();

        var after = await customer.GetFromJsonAsync<Profile>("/api/profiles/support", ApiFactory.Json);
        Assert.Equal(ApiFixture.SupportEmail, after!.Email);
        Assert.Equal("Trực ca sáng.", after.Biography);
        // Số điện thoại vẫn tắt: hai công tắc phải độc lập.
        Assert.Null(after.Phone);

        // Trả về trạng thái ban đầu để không ảnh hưởng test khác trong cùng fixture.
        (await support.PatchAsJsonAsync("/api/profiles/me",
            new { emailVisible = false, phone = "", biography = "" }, ApiFactory.Json)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Khong_sua_duoc_ho_so_nguoi_khac_va_khong_ton_tai_tra_404()
    {
        var customer = await _fx.CustomerAsync();

        Assert.Equal(HttpStatusCode.NotFound,
            (await customer.GetAsync("/api/profiles/khong-co-ai-ten-nay")).StatusCode);

        // `PATCH /api/profiles/me` chỉ sửa chính mình; không có đường nào nhận login của người khác.
        (await customer.PatchAsJsonAsync("/api/profiles/me",
            new { displayName = "Khách hàng A" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        // 404 chứ không phải 405: `PATCH` chỉ khai báo cho đường `me`, nên không có route nào
        // nhận login của người khác — không phải "có đường nhưng sai method".
        Assert.Equal(HttpStatusCode.NotFound,
            (await customer.PatchAsJsonAsync("/api/profiles/support",
                new { displayName = "Bị đổi trộm" }, ApiFactory.Json)).StatusCode);

        var support = await _fx.SupportAsync();
        var profile = await support.GetFromJsonAsync<Profile>("/api/profiles/support", ApiFactory.Json);
        Assert.Equal("Nhân viên hỗ trợ", profile!.DisplayName);
    }

    /// <summary>
    /// Đợt 8 — trạng thái hiện diện, và điểm phải làm cho đúng: <c>invisible</c> chặn ở server.
    ///
    /// Nếu server vẫn gửi trạng thái thật rồi để giao diện tự giấu thì mở tab Network là thấy, và
    /// một tính năng riêng tư hở như vậy còn tệ hơn không có — người dùng tưởng mình đang ẩn.
    /// </summary>
    [Fact]
    public async Task Trang_thai_invisible_khong_bao_gio_lo_ra_cho_nguoi_khac()
    {
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();

        // Không có kết nối real-time nào trong test này, nên mặc định là offline.
        var seen = await customer.GetFromJsonAsync<Profile>("/api/profiles/support", ApiFactory.Json);
        Assert.Equal("offline", seen!.Status);

        // Tự đặt snooze: người khác thấy đúng như vậy khi đang kết nối, và offline khi không.
        (await support.PatchAsJsonAsync("/api/profiles/me",
            new { presenceStatus = "Snooze" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        var mine = await support.GetFromJsonAsync<Profile>("/api/profiles/support", ApiFactory.Json);
        Assert.Equal("snooze", mine!.Status);

        // Đặt invisible: **chủ tài khoản** vẫn thấy trạng thái thật của mình...
        (await support.PatchAsJsonAsync("/api/profiles/me",
            new { presenceStatus = "Invisible" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        var self = await support.GetFromJsonAsync<Profile>("/api/profiles/support", ApiFactory.Json);
        Assert.Equal("invisible", self!.Status);

        // ...còn người khác nhận đúng chữ "offline", không kèm dấu hiệu nào để suy ra.
        var other = await customer.GetFromJsonAsync<Profile>("/api/profiles/support", ApiFactory.Json);
        Assert.Equal("offline", other!.Status);
        var raw = await customer.GetStringAsync("/api/profiles/support");
        Assert.DoesNotContain("invisible", raw, StringComparison.OrdinalIgnoreCase);

        // Trả về trạng thái ban đầu để không ảnh hưởng test khác.
        (await support.PatchAsJsonAsync("/api/profiles/me",
            new { presenceStatus = "Offline" }, ApiFactory.Json)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Doi_mat_khau_giet_phien_khac_nhung_giu_phien_dang_doi()
    {
        // Tài khoản riêng: đổi mật khẩu của tài khoản dùng chung sẽ làm hỏng mọi test khác.
        var admin = await _fx.AdminAsync();
        var email = $"doimatkhau-{Guid.NewGuid():N}@test.local";
        const string oldPassword = "Secret#12345";
        const string newPassword = "Secret#67890";

        (await admin.PostAsJsonAsync("/api/users",
            new { email, displayName = "Đổi mật khẩu", password = oldPassword, roles = new[] { "customer" } },
            ApiFactory.Json)).EnsureSuccessStatusCode();

        // Hai phiên độc lập của cùng một người.
        var phienA = await _fx.Factory.CreateClientAsAsync(email, oldPassword);
        var phienB = await _fx.Factory.CreateClientAsAsync(email, oldPassword);

        Assert.Equal(HttpStatusCode.OK, (await phienA.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phienB.GetAsync("/api/auth/me")).StatusCode);

        // Sai mật khẩu hiện tại thì không đổi được, dù đang cầm token hợp lệ.
        Assert.Equal(HttpStatusCode.Unauthorized, (await phienA.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "sai-hoan-toan", newPassword }, ApiFactory.Json)).StatusCode);

        var changed = await phienA.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = oldPassword, newPassword }, ApiFactory.Json);
        changed.EnsureSuccessStatusCode();

        // Phiên B chết ngay, không chờ token hết hạn.
        Assert.Equal(HttpStatusCode.Unauthorized, (await phienB.GetAsync("/api/auth/me")).StatusCode);

        // Phiên A cũng chết nếu vẫn dùng token cũ...
        Assert.Equal(HttpStatusCode.Unauthorized, (await phienA.GetAsync("/api/auth/me")).StatusCode);

        // ...nhưng token trả về ngay trong lượt đổi thì dùng được. Đây là thứ giữ cho người đổi
        // mật khẩu không bị đá ra ngoài mỗi lần đổi.
        var session = await changed.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        var fresh = _fx.Factory.CreateClient();
        fresh.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", session.GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/auth/me")).StatusCode);

        // Mật khẩu cũ hết tác dụng, mật khẩu mới đăng nhập được.
        var anon = _fx.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login",
            new { identifier = email, password = oldPassword }, ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/auth/login",
            new { identifier = email, password = newPassword }, ApiFactory.Json)).StatusCode);
    }
}
