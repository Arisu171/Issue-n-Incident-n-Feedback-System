using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// BR-CONC-01 áp cho Incident và Feedback, nhưng **có điều kiện**: <c>If-Match</c> chỉ trở thành
/// bắt buộc khi bản ghi đang bị người khác chiếm dụng để sửa.
///
/// Ba điều cần chứng minh:
///   1. Ngoài lúc tranh chấp, hành vi không đổi — sửa được mà không cần header nào.
///   2. Trong lúc tranh chấp, không có đường nào ghi đè mà không chứng minh mình đang nhìn bản
///      mới nhất: thiếu header <c>428</c>, lệch phiên bản <c>412</c>, và <c>*</c> không thay được.
///   3. Chỗ giữ không phải cái khoá: nó hết hạn, nhả được, và không tự làm khó chính người giữ.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EditClaimTests
{
    private readonly ApiFixture _fx;

    public EditClaimTests(ApiFixture fx) => _fx = fx;

    // ---------------- Ngoài lúc tranh chấp ----------------

    /// <summary>
    /// Không ai chiếm dụng thì mọi thứ y như cũ. Đây là nửa quan trọng nhất của quyết định
    /// "có điều kiện": một dòng `curl` sửa lỗi chính tả không phải đi hai vòng gọi.
    /// </summary>
    [Fact]
    public async Task Khong_ai_chiem_dung_thi_sua_duoc_ma_khong_can_if_match()
    {
        var customer = await _fx.CustomerAsync();
        var incident = await CreateIncidentAsync(customer, "Sự cố không ai tranh chấp");
        Assert.Equal(0, incident.Version);

        var updated = await PatchAsync<IncidentDto>(customer,
            $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Sửa thẳng, không cần header nào" });

        Assert.Equal(1, updated.Version);
    }

    /// <summary>
    /// Gửi <c>If-Match</c> lệch phiên bản thì bị từ chối dù không ai chiếm dụng: client đã tự
    /// nguyện đặt điều kiện, hệ thống phải tôn trọng điều kiện đó.
    /// </summary>
    [Fact]
    public async Task If_match_lech_phien_ban_bi_tu_choi_412()
    {
        var customer = await _fx.CustomerAsync();
        var incident = await CreateIncidentAsync(customer, "Sự cố bị gửi phiên bản cũ");

        var stale = await SendPatchAsync(customer, $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Ghi đè bằng một phiên bản đã cũ" }, ifMatch: "\"v7\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal(0, problem.GetProperty("currentVersion").GetInt32());
    }

    // ---------------- Trong lúc tranh chấp ----------------

    /// <summary>
    /// Người khác đang mở form sửa: lần ghi không kèm <c>If-Match</c> bị chặn ở <c>428</c>, và
    /// lỗi nói thẳng ai đang sửa — người dùng cần biết mình đang giẫm chân ai chứ không chỉ
    /// biết là mình bị chặn.
    /// </summary>
    [Fact]
    public async Task Nguoi_khac_dang_chiem_dung_thi_thieu_if_match_bi_tu_choi_428()
    {
        var customer = await _fx.CustomerAsync();
        var admin = await _fx.AdminAsync();

        var incident = await CreateIncidentAsync(customer, "Sự cố có hai người cùng mở form");
        await ClaimAsync(admin, $"/api/projects/support/incidents/{incident.Id}");

        var denied = await SendPatchAsync(customer, $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Ghi đè trong lúc người khác đang sửa" }, ifMatch: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, denied.StatusCode);

        var problem = await denied.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("\"v0\"", problem.GetProperty("etag").GetString());
        Assert.NotEmpty(problem.GetProperty("activeEditors").EnumerateArray());
    }

    /// <summary>Chặn là để buộc chứng minh, không phải để cấm: gửi đúng phiên bản thì ghi được.</summary>
    [Fact]
    public async Task Nguoi_khac_dang_chiem_dung_va_if_match_dung_thi_ghi_duoc()
    {
        var customer = await _fx.CustomerAsync();
        var admin = await _fx.AdminAsync();

        var incident = await CreateIncidentAsync(customer, "Sự cố ghi được nhờ gửi đúng phiên bản");
        await ClaimAsync(admin, $"/api/projects/support/incidents/{incident.Id}");

        var ok = await SendPatchAsync(customer, $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Ghi được vì đã chứng minh mình nhìn bản mới nhất" },
            ifMatch: $"\"v{incident.Version}\"");

        ok.EnsureSuccessStatusCode();
        var updated = (await ok.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json))!;
        Assert.Equal(incident.Version + 1, updated.Version);

        // Lần ghi thứ hai với đúng phiên bản cũ phải hỏng: phiên bản đã tiến lên một bước.
        var replay = await SendPatchAsync(customer, $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Gửi lại đúng phiên bản cũ" }, ifMatch: $"\"v{incident.Version}\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, replay.StatusCode);
    }

    /// <summary>
    /// <c>*</c> chỉ khẳng định "bản ghi có tồn tại", không nói gì về phiên bản — nên nó không
    /// trả lời được câu hỏi đang được hỏi, và không mở được cửa.
    /// </summary>
    [Fact]
    public async Task Dau_sao_khong_thay_duoc_if_match_khi_dang_bi_chiem_dung()
    {
        var customer = await _fx.CustomerAsync();
        var admin = await _fx.AdminAsync();

        var incident = await CreateIncidentAsync(customer, "Sự cố bị thử lách bằng dấu sao");
        await ClaimAsync(admin, $"/api/projects/support/incidents/{incident.Id}");

        var denied = await SendPatchAsync(customer, $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Lách bằng If-Match dấu sao" }, ifMatch: "*");

        Assert.Equal(HttpStatusCode.PreconditionRequired, denied.StatusCode);
    }

    // ---------------- Chỗ giữ không phải cái khoá ----------------

    /// <summary>
    /// Chỗ giữ của chính mình không được quay ra làm khó mình: chỉ người **khác** đang mở form
    /// mới siết điều kiện ghi.
    /// </summary>
    [Fact]
    public async Task Chinh_minh_giu_cho_thi_khong_tu_lam_kho_minh()
    {
        var customer = await _fx.CustomerAsync();
        var incident = await CreateIncidentAsync(customer, "Sự cố do chính mình giữ chỗ");

        var claim = await ClaimAsync(customer, $"/api/projects/support/incidents/{incident.Id}");
        Assert.Empty(claim.Others);

        var ok = await SendPatchAsync(customer, $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Tự sửa bài mình, không cần header" }, ifMatch: null);

        ok.EnsureSuccessStatusCode();
    }

    /// <summary>Đóng form thì trả chỗ, và điều kiện ghi lỏng lại ngay.</summary>
    [Fact]
    public async Task Nha_cho_xong_thi_khong_con_bat_buoc_if_match()
    {
        var customer = await _fx.CustomerAsync();
        var admin = await _fx.AdminAsync();
        var url = "/api/projects/support/incidents/";

        var incident = await CreateIncidentAsync(customer, "Sự cố được nhả chỗ sau khi giữ");
        await ClaimAsync(admin, url + incident.Id);

        Assert.Equal(HttpStatusCode.PreconditionRequired,
            (await SendPatchAsync(customer, url + incident.Id, new { title = "Bị chặn khi còn bị chiếm" }, null)).StatusCode);

        (await admin.DeleteAsync($"{url}{incident.Id}/edit-claim")).EnsureSuccessStatusCode();

        (await SendPatchAsync(customer, url + incident.Id,
            new { title = "Ghi được sau khi chỗ đã được nhả" }, null)).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Người dùng đóng máy giữa chừng là chuyện thường. Chỗ giữ hết hạn thì hết hiệu lực ngay,
    /// không cần tiến trình nào chạy đúng giờ — nếu không, một cái tab tắt từ hôm qua sẽ bắt
    /// cả đội gửi <c>If-Match</c> mãi mãi.
    /// </summary>
    [Fact]
    public async Task Cho_giu_het_han_thi_het_hieu_luc()
    {
        var customer = await _fx.CustomerAsync();
        var admin = await _fx.AdminAsync();
        var url = "/api/projects/support/incidents/";

        var incident = await CreateIncidentAsync(customer, "Sự cố có chỗ giữ bị bỏ quên");
        await ClaimAsync(admin, url + incident.Id);

        await using (var db = _fx.Factory.CreateDbContext())
        {
            await db.EditClaims
                .Where(c => c.EntityType == EditableEntityType.Incident && c.EntityId == incident.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.ClaimedAt, DateTimeOffset.UtcNow.AddHours(-2))
                    .SetProperty(c => c.ExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)));
        }

        (await SendPatchAsync(customer, url + incident.Id,
            new { title = "Ghi được vì chỗ giữ đã hết hạn" }, null)).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Giữ chỗ đòi đúng quyền như đường ghi. Thiếu vế này thì bất kỳ ai đọc được sự cố cũng ép
    /// được cả đội phải gửi <c>If-Match</c> — một đường quấy rối không tốn gì để thực hiện.
    /// </summary>
    [Fact]
    public async Task Nguoi_khong_sua_duoc_noi_dung_thi_khong_giu_cho_duoc()
    {
        var customer = await _fx.CustomerAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(customer, "Sự cố mà người xử lý muốn giữ chỗ");

        var denied = await responder.PutAsync(
            $"/api/projects/support/incidents/{incident.Id}/edit-claim", null);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    /// <summary>Hai người cùng mở form thì mỗi người nhìn thấy phía kia.</summary>
    [Fact]
    public async Task Cho_giu_liet_ke_nguoi_khac_dang_mo_form()
    {
        var customer = await _fx.CustomerAsync();
        var admin = await _fx.AdminAsync();
        var url = $"/api/projects/support/incidents/";

        var incident = await CreateIncidentAsync(customer, "Sự cố có hai người cùng mở");

        Assert.Empty((await ClaimAsync(customer, url + incident.Id)).Others);

        var adminClaim = await ClaimAsync(admin, url + incident.Id);
        var seen = Assert.Single(adminClaim.Others);
        Assert.Equal(incident.Reporter.Id, seen.User.Id);

        // Gia hạn chỗ của chính mình không nhân bản thành hai hàng.
        Assert.Single((await ClaimAsync(customer, url + incident.Id)).Others);
    }

    /// <summary>
    /// Phiên bản trả lời câu hỏi "bản ghi đã khác đi chưa", không đếm số ô đã sửa — nên một lần
    /// lưu đổi ba trường vẫn chỉ tăng một bước.
    /// </summary>
    [Fact]
    public async Task Mot_lan_sua_nhieu_truong_chi_tang_mot_phien_ban()
    {
        var customer = await _fx.CustomerAsync();
        var incident = await CreateIncidentAsync(customer, "Sự cố sửa ba trường một lần");

        var updated = await PatchAsync<IncidentDto>(customer,
            $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Đổi cả tiêu đề", description = "cả mô tả", severity = "High" });

        Assert.Equal(incident.Version + 1, updated.Version);
    }

    /// <summary>Chuyển trạng thái không đụng vào các ô của form sửa, nên không được bump phiên bản.</summary>
    [Fact]
    public async Task Chuyen_trang_thai_khong_lam_cu_phien_ban_cua_form_sua()
    {
        var customer = await _fx.CustomerAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(customer, "Sự cố được chuyển trạng thái giữa chừng");

        var moved = await responder.PatchAsJsonAsync(
            $"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);
        moved.EnsureSuccessStatusCode();

        // Form sửa mở từ trước vẫn còn khớp: If-Match cũ vẫn phải đi lọt.
        (await SendPatchAsync(customer, $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Form mở từ trước vẫn lưu được" },
            ifMatch: $"\"v{incident.Version}\"")).EnsureSuccessStatusCode();
    }

    // ---------------- Cùng quy tắc cho Feedback ----------------

    [Fact]
    public async Task Phan_hoi_dang_bi_chiem_dung_cung_doi_if_match()
    {
        var customer = await _fx.CustomerAsync();
        var support = await _fx.SupportAsync();

        var feedback = await CreateFeedbackAsync(customer, "Phản hồi bị hai người cùng mở form sửa.");
        await ClaimAsync(support, $"/api/projects/support/feedbacks/{feedback.Id}");

        var denied = await SendPatchAsync(customer, $"/api/projects/support/feedbacks/{feedback.Id}",
            new { content = "Khách hàng sửa trong lúc nhân viên đang mở form." }, ifMatch: null);

        Assert.Equal(HttpStatusCode.PreconditionRequired, denied.StatusCode);

        (await SendPatchAsync(customer, $"/api/projects/support/feedbacks/{feedback.Id}",
            new { content = "Khách hàng sửa, có kèm đúng phiên bản." },
            ifMatch: $"\"v{feedback.Version}\"")).EnsureSuccessStatusCode();
    }

    // ---------------- Helpers ----------------

    private static async Task<HttpResponseMessage> SendPatchAsync(
        HttpClient client, string url, object body, string? ifMatch)
    {
        using var message = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body, options: ApiFactory.Json)
        };

        if (ifMatch is not null)
        {
            // TryAddWithoutValidation: `*` không qua được bộ kiểm ETag của HttpClient, mà đó lại
            // đúng là một trong những trường hợp cần thử.
            message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return await client.SendAsync(message);
    }

    private static async Task<T> PatchAsync<T>(HttpClient client, string url, object body)
    {
        var response = await SendPatchAsync(client, url, body, ifMatch: null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;
    }

    private static async Task<ClaimDto> ClaimAsync(HttpClient client, string entityUrl)
    {
        var response = await client.PutAsync($"{entityUrl}/edit-claim", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClaimDto>(ApiFactory.Json))!;
    }

    private static async Task<IncidentDto> CreateIncidentAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/projects/support/incidents",
            new { title, severity = "Medium" }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json))!;
    }

    private static async Task<FeedbackDto> CreateFeedbackAsync(HttpClient client, string content)
    {
        var response = await client.PostAsJsonAsync("/api/projects/support/feedbacks",
            new { channel = "Web", content }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json))!;
    }

    private sealed record UserRefDto(Guid Id, string DisplayName, string Email);

    private sealed record ActiveEditorDto(UserRefDto User, DateTimeOffset Since, DateTimeOffset ExpiresAt);

    private sealed record ClaimDto(DateTimeOffset ExpiresAt, int Version, List<ActiveEditorDto> Others);

    private sealed record IncidentDto(Guid Id, string Title, string Status, UserRefDto Reporter, int Version);

    private sealed record FeedbackDto(Guid Id, string Content, int Version);
}
