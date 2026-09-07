using System.Net.Http.Json;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Incidents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static IncidentTracker.Api.Tests.Integration.IncidentLifecycleTests;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>Mục 6.9 — cảnh báo SLA và các signal quan sát được.</summary>
[Collection(ApiCollection.Name)]
public class SlaAndMetricsTests
{
    private readonly ApiFixture _fx;

    public SlaAndMetricsTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Su_co_moi_tao_chua_vuot_nguong_SLA()
    {
        var support = await _fx.SupportAsync();
        var incident = await CreateIncidentAsync(support, "Sự cố mới chưa quá hạn");

        var detail = await support.GetFromJsonAsync<IncidentWithSlaDto>(
            $"/api/projects/support/incidents/{incident.Id}", ApiFactory.Json);

        Assert.NotNull(detail!.Sla);
        Assert.False(detail.Sla!.Breached);
        Assert.Equal(24, detail.Sla.ThresholdHours);
    }

    /// <summary>
    /// Lùi <c>created_at</c> thẳng trong DB để mô phỏng một sự cố tồn đọng quá 24 giờ —
    /// đây là cách duy nhất kiểm chứng ngưỡng mà không phải chờ thật.
    /// </summary>
    [Fact]
    public async Task Su_co_ton_dong_qua_24h_bi_danh_dau_vuot_nguong()
    {
        var support = await _fx.SupportAsync();
        var incident = await CreateIncidentAsync(support, "Sự cố tồn đọng quá lâu");

        await BackdateAsync(incident.Id, TimeSpan.FromHours(30));

        var detail = await support.GetFromJsonAsync<IncidentWithSlaDto>(
            $"/api/projects/support/incidents/{incident.Id}", ApiFactory.Json);

        Assert.True(detail!.Sla!.Breached);
        Assert.True(detail.Sla.ElapsedHours >= 30);
        Assert.True(detail.Sla.RemainingHours < 0);
    }

    [Fact]
    public async Task Bo_loc_slaBreachedOnly_chi_tra_su_co_qua_han()
    {
        var support = await _fx.SupportAsync();

        var fresh = await CreateIncidentAsync(support, "Sự cố còn trong hạn SLA");
        var stale = await CreateIncidentAsync(support, "Sự cố đã quá hạn SLA");
        await BackdateAsync(stale.Id, TimeSpan.FromHours(48));

        var page = await support.GetFromJsonAsync<PagedDto<IncidentWithSlaDto>>(
            "/api/projects/support/incidents?slaBreachedOnly=true&pageSize=100", ApiFactory.Json);

        Assert.Contains(page!.Items, i => i.Id == stale.Id);
        Assert.DoesNotContain(page.Items, i => i.Id == fresh.Id);
        Assert.All(page.Items, i => Assert.True(i.Sla!.Breached));
    }

    /// <summary>Sự cố đã đóng ngừng tính giờ nên không bao giờ lọt vào danh sách cảnh báo.</summary>
    [Fact]
    public async Task Su_co_da_dong_khong_con_bi_tinh_SLA()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(support, "Sự cố cũ nhưng đã đóng");
        await BackdateAsync(incident.Id, TimeSpan.FromHours(200));
        await TransitionAsync(responder, incident.Id, "Mitigating");
        await TransitionAsync(responder, incident.Id, "Resolved");

        var detail = await support.GetFromJsonAsync<IncidentWithSlaDto>(
            $"/api/projects/support/incidents/{incident.Id}", ApiFactory.Json);
        Assert.Null(detail!.Sla);

        var page = await support.GetFromJsonAsync<PagedDto<IncidentWithSlaDto>>(
            "/api/projects/support/incidents?slaBreachedOnly=true&pageSize=100", ApiFactory.Json);
        Assert.DoesNotContain(page!.Items, i => i.Id == incident.Id);
    }

    /// <summary>Job nền chạy được và không ném lỗi khi có sự cố vượt ngưỡng.</summary>
    [Fact]
    public async Task Job_nen_quet_duoc_va_cap_nhat_metric()
    {
        var support = await _fx.SupportAsync();
        var incident = await CreateIncidentAsync(support, "Sự cố cho job nền quét");
        await BackdateAsync(incident.Id, TimeSpan.FromHours(72));

        var monitor = _fx.Factory.Services.GetServices<IHostedService>()
            .OfType<SlaMonitor>().Single();

        await monitor.ScanAsync(CancellationToken.None);

        await using var db = _fx.Factory.CreateDbContext();
        var breachedCount = await db.Incidents
            .CountAsync(i => i.Status == IncidentStatus.Investigating
                             && i.CreatedAt < DateTimeOffset.UtcNow.AddHours(-24));
        Assert.True(breachedCount >= 1);
    }

    [Fact]
    public async Task Endpoint_metrics_phoi_signal_cho_Prometheus()
    {
        var client = _fx.Factory.CreateClient();

        // Sinh một lần đăng nhập thất bại để chắc chắn counter có giá trị.
        await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = "khong-ton-tai@test.local", password = "sai-mat-khau" }, ApiFactory.Json);

        var response = await client.GetAsync("/metrics");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("incident_login_failures_total", body);
        Assert.Contains("incident_sla_breached", body);
        Assert.Contains("http_server_request_duration_seconds", body);
    }

    private async Task BackdateAsync(Guid incidentId, TimeSpan age)
    {
        await using var db = _fx.Factory.CreateDbContext();
        var target = DateTimeOffset.UtcNow - age;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE incidents SET created_at = {target} WHERE id = {incidentId}");
    }

    public sealed record SlaDto(
        bool Breached, int? ThresholdHours, double ElapsedHours, double? RemainingHours);

    public sealed record IncidentWithSlaDto(Guid Id, string Title, string Status, SlaDto? Sla);
}
