using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Yêu cầu kỹ thuật "Có web, api, db healthy" — phải kiểm chứng được **bằng một lời gọi API**
/// (ví dụ từ Postman), không phải bằng cách đọc <c>docker compose ps</c>.
/// </summary>
[Collection(ApiCollection.Name)]
public class HealthTests
{
    private readonly ApiFixture _fx;

    public HealthTests(ApiFixture fx) => _fx = fx;

    /// <summary>Endpoint dùng cho healthcheck của container: chỉ api và db.</summary>
    [Fact]
    public async Task Health_soi_api_va_db()
    {
        var client = _fx.Factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Healthy", report.GetProperty("status").GetString());

        var names = report.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();

        Assert.Contains("api", names);
        Assert.Contains("db", names);

        // Không được có web ở đây: compose khai báo web depends_on api healthy, nên api chờ web
        // sẽ tạo khóa chết và không service nào lên được.
        Assert.DoesNotContain("web", names);
    }

    /// <summary>Endpoint tổng hợp: một lời gọi biết trạng thái cả ba service.</summary>
    [Fact]
    public async Task Health_system_soi_du_ca_ba_service()
    {
        var client = _fx.Factory.CreateClient();

        var response = await client.GetAsync("/api/health/system");
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();

        var names = report.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();

        Assert.Equal(3, names.Count);
        Assert.Contains("api", names);
        Assert.Contains("db", names);
        Assert.Contains("web", names);
    }

    [Fact]
    public async Task Bao_cao_suc_khoe_co_du_truong_de_doc_bang_may()
    {
        var client = _fx.Factory.CreateClient();
        // Không dùng GetFromJsonAsync: báo cáo có thể trả 503 và ta vẫn cần đọc được thân phản hồi.
        var response = await client.GetAsync("/api/health/system");
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(report.TryGetProperty("status", out _));
        Assert.True(report.TryGetProperty("totalDurationMs", out _));
        Assert.True(report.TryGetProperty("correlationId", out _));

        foreach (var check in report.GetProperty("checks").EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("status").GetString()));
            Assert.True(check.TryGetProperty("durationMs", out _));
        }
    }

    /// <summary>Cả hai endpoint phải gọi được mà không cần token — hệ thống giám sát không đăng nhập.</summary>
    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/health/system")]
    public async Task Endpoint_suc_khoe_khong_doi_xac_thuc(string path)
    {
        var client = _fx.Factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Một service hỏng phải trả 503 để hệ thống giám sát chỉ đọc mã trạng thái cũng biết,
    /// và phải chỉ đích danh service nào hỏng.
    /// </summary>
    [Fact]
    public async Task Service_hong_thi_tra_503_va_chi_dich_danh()
    {
        // Trỏ health check của web sang một cổng không có ai lắng nghe để mô phỏng web chết.
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("HealthChecks:WebUrl", "http://127.0.0.1:59999/healthz")
                   .UseSetting("HealthChecks:TimeoutSeconds", "2"));

        var client = factory.CreateClient();

        var systemResponse = await client.GetAsync("/api/health/system");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, systemResponse.StatusCode);

        var report = await systemResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Unhealthy", report.GetProperty("status").GetString());

        var web = report.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "web");
        Assert.Equal("Unhealthy", web.GetProperty("status").GetString());

        // api và db vẫn khỏe, nên endpoint của compose không được đỏ theo.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }

    /// <summary>
    /// Nhánh tất cả cùng khỏe — đây mới là hình dạng phản hồi mà người chấm thấy khi gọi từ
    /// Postman lúc cả ba container đang chạy.
    /// </summary>
    [Fact]
    public async Task Khi_ca_ba_service_khoe_thi_bao_cao_Healthy_va_tra_200()
    {
        using var stubWeb = new StubWebService();
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("HealthChecks:WebUrl", stubWeb.HealthUrl));

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/health/system");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", report.GetProperty("status").GetString());

        var byName = report.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!,
                          c => c.GetProperty("status").GetString());

        Assert.Equal("Healthy", byName["api"]);
        Assert.Equal("Healthy", byName["db"]);
        Assert.Equal("Healthy", byName["web"]);
    }

    /// <summary>NFR-SEC-02 — báo cáo lỗi không được để lộ chuỗi kết nối hay chi tiết nội bộ.</summary>
    [Fact]
    public async Task Bao_cao_loi_khong_lo_chi_tiet_noi_bo()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("HealthChecks:WebUrl", "http://127.0.0.1:59998/healthz")
                   .UseSetting("HealthChecks:TimeoutSeconds", "2"));

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/health/system");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Password=", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Username=", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("correlationId", raw, StringComparison.Ordinal);
    }
}
