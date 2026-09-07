using System.Net;
using System.Net.Http.Json;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// BR-SEC-01 · BR-BIZ-07 — khách hàng tự gắn phản hồi của mình vào sự cố của mình.
///
/// Quyền <c>feedback.link</c> mở cho vai trò thấp nhất trong hệ thống, nên ranh giới phải được
/// chốt bằng test ở **cả hai đầu**: phản hồi phải là của người gọi, và sự cố phải là sự cố người
/// gọi đọc được. Trước khi có bộ test này, <c>LinkAsync</c> chỉ soát đầu sự cố — khách A gắn
/// được phản hồi của khách B vào sự cố của A, và câu trả lời của endpoint trả nguyên nội dung
/// phản hồi của B về cho A.
/// </summary>
[Collection("api")]
public sealed class CustomerFeedbackLinkTests
{
    private readonly ApiFixture _fx;

    public CustomerFeedbackLinkTests(ApiFixture fx) => _fx = fx;

    /// <summary>Đường đi đúng: phản hồi của mình, sự cố của mình.</summary>
    [Fact]
    public async Task Khach_gan_duoc_phan_hoi_cua_minh_vao_su_co_cua_minh()
    {
        var alice = await _fx.CustomerAsync();

        var incident = await CreateIncidentAsync(alice, "Khách tự gắn phản hồi vào sự cố của mình");
        var feedback = await CreateFeedbackAsync(alice, "Bổ sung thông tin cho sự cố tôi đã gửi.");

        var linked = await alice.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/link",
            new { incidentId = incident.Id }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var after = await linked.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json);
        Assert.Equal(incident.Id, after!.IncidentId);

        // BR-BIZ-12 — gắn vào sự cố nghĩa là đã có người tiếp nhận.
        Assert.Equal("Acknowledged", after.Status);

        // BR-BIZ-07 — gắn lại cùng sự cố là idempotent.
        Assert.Equal(HttpStatusCode.OK,
            (await alice.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/link",
                new { incidentId = incident.Id }, ApiFactory.Json)).StatusCode);
    }

    /// <summary>
    /// Hồi quy cho lỗ hổng: phản hồi của khách khác phải trả <b>404</b>, không phải 403.
    /// 403 xác nhận rằng phản hồi đó có thật, tức vẫn rò một bit thông tin.
    /// </summary>
    [Fact]
    public async Task Khach_khong_gan_duoc_phan_hoi_cua_khach_khac()
    {
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();

        var feedbackOfBob = await CreateFeedbackAsync(bob, "Phản hồi riêng của khách B.");
        var incidentOfAlice = await CreateIncidentAsync(alice, "Sự cố của khách A");

        var response = await alice.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedbackOfBob.Id}/link",
            new { incidentId = incidentOfAlice.Id }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Và phản hồi của B không bị đụng vào.
        var support = await _fx.SupportAsync();
        var reloaded = await support.GetFromJsonAsync<FeedbackDto>(
            $"/api/projects/support/feedbacks/{feedbackOfBob.Id}", ApiFactory.Json);
        Assert.Null(reloaded!.IncidentId);
    }

    /// <summary>Đầu còn lại: phản hồi của mình nhưng sự cố của người khác vẫn bị chặn.</summary>
    [Fact]
    public async Task Khach_khong_gan_duoc_vao_su_co_cua_khach_khac()
    {
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();

        var feedbackOfAlice = await CreateFeedbackAsync(alice, "Phản hồi của khách A.");
        var incidentOfBob = await CreateIncidentAsync(bob, "Sự cố của khách B");

        var response = await alice.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedbackOfAlice.Id}/link",
            new { incidentId = incidentOfBob.Id }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Support có <c>feedback.read.all</c> nên vẫn gắn được phản hồi của khách.</summary>
    [Fact]
    public async Task Support_van_gan_duoc_phan_hoi_cua_khach()
    {
        var alice = await _fx.CustomerAsync();
        var support = await _fx.SupportAsync();

        var feedbackOfAlice = await CreateFeedbackAsync(alice, "Phản hồi để support phân loại.");
        var incident = await CreateIncidentAsync(support, "Sự cố do support ghi nhận");

        var response = await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedbackOfAlice.Id}/link",
            new { incidentId = incident.Id }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------- Helpers ----------------

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

    private sealed record IncidentDto(Guid Id, string Title, string Status);

    private sealed record FeedbackDto(Guid Id, string Status, Guid? IncidentId);
}
