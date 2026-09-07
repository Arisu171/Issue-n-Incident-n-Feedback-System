using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using static IncidentTracker.Api.Tests.Integration.IncidentLifecycleTests;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Mục 6.6 + checklist 8.3 — mọi lỗi phải cùng một định dạng ProblemDetails kèm correlationId.
/// 401 và 403 do middleware sinh ra là trường hợp dễ bị bỏ sót nhất vì chúng không ném exception.
/// </summary>
[Collection(ApiCollection.Name)]
public class ErrorContractTests
{
    private readonly ApiFixture _fx;

    public ErrorContractTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Thieu_token_tra_401_theo_dinh_dang_ProblemDetails()
    {
        var client = _fx.Factory.CreateClient();
        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertProblemDetailsAsync(response, 401);
    }

    [Fact]
    public async Task Token_het_han_tra_401_theo_dinh_dang_ProblemDetails()
    {
        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenBuilder.Expired());

        var response = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await AssertProblemDetailsAsync(response, 401);

        // NFR-SEC-02: không tiết lộ token hỏng ở đâu.
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.DoesNotContain("signature", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expired", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Thieu_permission_tra_403_kem_ten_permission_con_thieu()
    {
        var readOnly = await _fx.ReadOnlyAsync();
        var response = await readOnly.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await AssertProblemDetailsAsync(response, 403);
        Assert.Equal("user.read", problem.GetProperty("requiredPermission").GetString());
    }

    /// <summary>
    /// 403 do action filter và 403 do policy phải cùng một hình dạng, nếu không client
    /// sẽ phải xử lý hai kiểu khác nhau cho cùng một mã trạng thái.
    /// </summary>
    [Fact]
    public async Task Hai_nguon_sinh_403_tra_ve_cung_mot_hinh_dang()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var mitigator = await _fx.MitigatorAsync();

        // 403 từ policy: support không có incident.update_status.
        var incident = await CreateIncidentAsync(support, "So sánh hai nguồn sinh 403");
        var fromPolicy = await support.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);

        // 403 từ action filter: mitigator có update_status nhưng không có incident.resolve.
        await TransitionAsync(responder, incident.Id, "Mitigating");
        var fromFilter = await mitigator.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Resolved" }, ApiFactory.Json);

        var a = await AssertProblemDetailsAsync(fromPolicy, 403);
        var b = await AssertProblemDetailsAsync(fromFilter, 403);

        Assert.Equal("incident.update_status", a.GetProperty("requiredPermission").GetString());
        Assert.Equal("incident.resolve", b.GetProperty("requiredPermission").GetString());
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Loi_409_transition_van_giu_dung_dinh_dang()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var incident = await CreateIncidentAsync(support, "Kiểm tra định dạng lỗi 409");

        var response = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Resolved" }, ApiFactory.Json);

        var problem = await AssertProblemDetailsAsync(response, 409);
        Assert.Equal("Mitigating", problem.GetProperty("allowedNextStatus").GetString());
    }

    [Fact]
    public async Task Loi_404_van_giu_dung_dinh_dang()
    {
        var responder = await _fx.ResponderAsync();
        var response = await responder.GetAsync($"/api/projects/support/incidents/{Guid.NewGuid()}");

        await AssertProblemDetailsAsync(response, 404);
    }

    /// <summary>Correlation id do client gửi lên được giữ nguyên để nối log hai phía.</summary>
    [Fact]
    public async Task Correlation_id_cua_client_duoc_giu_nguyen()
    {
        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "test-correlation-123");

        var response = await client.GetAsync("/api/users");
        var problem = await AssertProblemDetailsAsync(response, 401);

        Assert.Equal("test-correlation-123", problem.GetProperty("correlationId").GetString());
        Assert.Equal("test-correlation-123", response.Headers.GetValues("X-Correlation-ID").Single());
    }

    /// <summary>Header bẩn bị loại bỏ và thay bằng id mới, tránh log forging.</summary>
    [Fact]
    public async Task Correlation_id_khong_hop_le_bi_thay_the()
    {
        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "gia-mao\tnewline-injection");

        var response = await client.GetAsync("/api/health");

        var echoed = response.Headers.GetValues("X-Correlation-ID").Single();
        Assert.DoesNotContain("\t", echoed);
        Assert.Matches("^[A-Za-z0-9_-]+$", echoed);
    }

    private static async Task<JsonElement> AssertProblemDetailsAsync(
        HttpResponseMessage response, int expectedStatus)
    {
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);

        Assert.Equal(expectedStatus, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("correlationId").GetString()));

        return problem;
    }
}
