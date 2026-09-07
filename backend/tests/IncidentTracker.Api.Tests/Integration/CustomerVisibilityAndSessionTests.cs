using System.Net;
using System.Net.Http.Json;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Hai ranh giới được chốt ở đây:
///
/// 1. <b>Ghi chú chuyển trạng thái là dữ liệu vận hành.</b> Ô nhập chỉ ghi "Ghi chú (tùy chọn)",
///    không hề báo nó sẽ hiện cho khách, nên nhân viên có lý khi viết phân tích nội bộ vào đó.
///    Khách phải thấy đủ mốc thời gian và người đổi, nhưng không thấy nội dung ghi chú.
///
/// 2. <b>Phiên trượt.</b> Token vẫn ngắn hạn, nhưng được cấp lại khi người dùng còn thao tác,
///    và có trần tuyệt đối để phiên trượt không thành phiên vĩnh viễn.
/// </summary>
[Collection("api")]
public sealed class CustomerVisibilityAndSessionTests
{
    private readonly ApiFixture _fx;

    public CustomerVisibilityAndSessionTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Khach_thay_lich_su_nhung_khong_thay_ghi_chu_noi_bo()
    {
        var customer = await _fx.CustomerAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(customer, "Khách theo dõi tiến độ xử lý");

        var moved = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating", note = "Nghi do rotate key ở vault, chưa báo khách." },
            ApiFactory.Json);
        moved.EnsureSuccessStatusCode();

        // Người trong đội thấy đủ nội dung.
        var staffView = await responder.GetFromJsonAsync<List<HistoryDto>>(
            $"/api/projects/support/incidents/{incident.Id}/history", ApiFactory.Json);
        Assert.Single(staffView!);
        Assert.Equal("Nghi do rotate key ở vault, chưa báo khách.", staffView![0].Note);

        // Xem được **mọi sự cố** không kéo theo xem được ghi chú nội bộ. Tài khoản chỉ đọc có
        // `incident.read.all` nhưng không có `incident.note.read` — đúng hình dạng quyền mà
        // khách hàng sắp nhận. Nếu hai điều đó còn dùng chung một permission thì dòng dưới sẽ
        // trả về nguyên văn ghi chú.
        var readOnly = await _fx.ReadOnlyAsync();
        var readOnlyView = await readOnly.GetFromJsonAsync<List<HistoryDto>>(
            $"/api/projects/support/incidents/{incident.Id}/history", ApiFactory.Json);
        Assert.Single(readOnlyView!);
        Assert.Equal("Mitigating", readOnlyView![0].ToStatus);
        Assert.Null(readOnlyView[0].Note);

        // Khách vẫn thấy dòng lịch sử, nhưng ghi chú bị cắt.
        var customerView = await customer.GetFromJsonAsync<List<HistoryDto>>(
            $"/api/projects/support/incidents/{incident.Id}/history", ApiFactory.Json);
        Assert.Single(customerView!);
        Assert.Equal("Investigating", customerView![0].FromStatus);
        Assert.Equal("Mitigating", customerView[0].ToStatus);
        Assert.Null(customerView[0].Note);
    }

    [Fact]
    public async Task Phien_duoc_cap_lai_khi_nguoi_dung_con_hoat_dong()
    {
        var client = await _fx.CustomerAsync();

        var response = await client.PostAsync("/api/auth/refresh", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var session = await response.Content.ReadFromJsonAsync<SessionDto>(ApiFactory.Json);
        Assert.False(string.IsNullOrWhiteSpace(session!.AccessToken));
        Assert.True(session.ExpiresAt > DateTimeOffset.UtcNow);

        // Token mới dùng được ngay.
        using var renewed = _fx.Factory.CreateClient();
        renewed.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await renewed.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Khong_co_token_thi_khong_cap_lai_duoc_phien()
    {
        using var anonymous = _fx.Factory.CreateClient();

        var response = await anonymous.PostAsync("/api/auth/refresh", content: null);

        // Phiên trượt nối tiếp một phiên đang sống, không hồi sinh phiên đã chết.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---------------- Helpers ----------------

    private static async Task<IncidentDto> CreateIncidentAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/projects/support/incidents",
            new { title, severity = "Medium" }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json))!;
    }

    private sealed record IncidentDto(Guid Id, string Title, string Status);

    private sealed record HistoryDto(Guid Id, string FromStatus, string ToStatus, string? Note);

    private sealed record SessionDto(string AccessToken, DateTimeOffset ExpiresAt);
}
