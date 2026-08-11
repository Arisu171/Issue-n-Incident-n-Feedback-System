using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Observability;
using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Incidents;

/// <summary>
/// Mục 6.9 — "cảnh báo khi có sự cố ở Investigating quá 24 giờ".
///
/// Job nền quét định kỳ và ghi log cảnh báo kèm đầy đủ trường để hệ thống giám sát bên ngoài
/// bắt được; đồng thời cập nhật metric <c>incident_sla_breached</c> cho Prometheus.
/// Cố ý không gửi email/Slack: mục 1.3 đã đưa tích hợp thông báo ra ngoài phạm vi.
/// </summary>
public sealed class SlaMonitor : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<SlaOptions> _options;
    private readonly AppMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly ILogger<SlaMonitor> _logger;

    public SlaMonitor(IServiceScopeFactory scopeFactory, IOptionsMonitor<SlaOptions> options,
        AppMetrics metrics, TimeProvider clock, ILogger<SlaMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.CurrentValue.ScanIntervalMinutes;
        if (interval <= 0)
        {
            _logger.LogInformation("SlaMonitor đã tắt (Sla:ScanIntervalMinutes = 0).");
            return;
        }

        _logger.LogInformation(
            "SlaMonitor chạy mỗi {Interval} phút; ngưỡng Investigating {Investigating}h, Mitigating {Mitigating}h.",
            interval, _options.CurrentValue.InvestigatingHours, _options.CurrentValue.MitigatingHours);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(interval));

        do
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Job nền không được phép làm sập host chỉ vì DB tạm thời không kết nối được.
                _logger.LogError(ex, "SlaMonitor quét thất bại; sẽ thử lại ở chu kỳ sau.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ScanAsync(CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var now = _clock.GetUtcNow();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var investigatingCutoff = now.AddHours(-options.InvestigatingHours);
        var mitigatingCutoff = now.AddHours(-options.MitigatingHours);

        // Lọc ngay trong SQL để không kéo cả bảng lên bộ nhớ; partial index
        // ix_incidents_status_created_at phục vụ đúng truy vấn này.
        var breached = await db.Incidents.AsNoTracking()
            .Where(i =>
                (i.Status == IncidentStatus.Investigating && i.CreatedAt < investigatingCutoff)
                || (i.Status == IncidentStatus.Mitigating
                    && (i.MitigatingAt ?? i.CreatedAt) < mitigatingCutoff))
            .Select(i => new { i.Id, i.Title, i.Status, i.Severity, i.CreatedAt, i.MitigatingAt, i.AssigneeId })
            .ToListAsync(ct);

        _metrics.SetSlaBreached(breached.Count);

        if (breached.Count == 0)
        {
            _logger.LogDebug("SlaMonitor: không có sự cố nào vượt ngưỡng SLA.");
            return;
        }

        _logger.LogWarning("SlaMonitor: {Count} sự cố đã vượt ngưỡng SLA.", breached.Count);

        foreach (var incident in breached)
        {
            var sla = SlaEvaluator.Evaluate(
                options, incident.Status, incident.CreatedAt, incident.MitigatingAt, now);

            _logger.LogWarning(
                "Alert sla.breach incident={IncidentId} status={Status} severity={Severity} "
                + "elapsedHours={ElapsedHours} thresholdHours={ThresholdHours} assignee={AssigneeId}",
                incident.Id, incident.Status, incident.Severity,
                sla?.ElapsedHours, sla?.ThresholdHours, incident.AssigneeId);
        }
    }
}
