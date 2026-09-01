using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Tickets.Infrastructure;

/// <summary>
/// Job bảo trì (Architecture v3.1): BR-SCALE-02 tạo sẵn partition tháng hiện tại + tháng kế cho
/// <c>ticket_events</c>; BR-REL-01 dọn Idempotency-Key hết hạn. Chạy ngay khi khởi động rồi định kỳ.
/// </summary>
public sealed class TicketMaintenanceJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<TicketingOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TicketMaintenanceJob> _logger;

    public TicketMaintenanceJob(IServiceScopeFactory scopes, IOptionsMonitor<TicketingOptions> options,
        TimeProvider clock, ILogger<TicketMaintenanceJob> logger)
    {
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.CurrentValue.MaintenanceIntervalMinutes;
        if (interval <= 0)
        {
            _logger.LogInformation("TicketMaintenanceJob đã tắt (Ticketing:MaintenanceIntervalMinutes = 0).");
            return;
        }

        // Chờ DatabaseInitializer chạy migration xong.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(interval));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TicketMaintenanceJob thất bại; thử lại ở chu kỳ sau.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = _clock.GetUtcNow();

        var thisMonth = new DateOnly(now.Year, now.Month, 1);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"select ticket_events_ensure_partition({thisMonth}), ticket_events_ensure_partition({thisMonth.AddMonths(1)})", ct);

        var removed = await db.IdempotencyKeys.Where(k => k.ExpiresAt < now).ExecuteDeleteAsync(ct);
        _logger.LogInformation("TicketMaintenanceJob: partition {Month}/{Next} sẵn sàng, dọn {Removed} idempotency key hết hạn.",
            thisMonth, thisMonth.AddMonths(1), removed);
    }
}
