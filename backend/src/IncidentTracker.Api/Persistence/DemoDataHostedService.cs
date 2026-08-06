using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Persistence;

/// <summary>
/// Seed dữ liệu trình diễn ở NỀN, sau khi API đã mở cổng.
///
/// <b>Vì sao phải tách ra.</b> Khối này dựng hàng chục ticket kèm bình luận và reaction, mỗi
/// bản ghi một vòng đi–về tới database. Chạy nó trước <c>app.Run()</c> nghĩa là tiến trình
/// chưa nghe cổng nào trong suốt thời gian đó — và nền tảng lưu trữ nào cũng có giới hạn chờ
/// cổng. Quá hạn thì tiến trình bị giết giữa chừng, để lại database seed dở dang.
///
/// Migration và dữ liệu tham chiếu vẫn chặn khởi động: thiếu schema thì API không phục vụ nổi
/// request nào. Dữ liệu trình diễn thì khác — thiếu nó API vẫn chạy đúng, chỉ là màn hình trống.
/// </summary>
public sealed class DemoDataHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly SeedOptions _options;
    private readonly ILogger<DemoDataHostedService> _logger;

    public DemoDataHostedService(IServiceProvider services, IOptions<SeedOptions> options,
        ILogger<DemoDataHostedService> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.DemoData)
        {
            return;
        }

        // Nhường cho vòng khởi động dựng xong đường ống và mở cổng trước.
        await Task.Yield();

        try
        {
            await DemoDataSeeder.SeedAsync(_services, _logger, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Dừng seed dữ liệu trình diễn vì ứng dụng đang tắt.");
        }
        catch (Exception ex)
        {
            // Dữ liệu trình diễn hỏng KHÔNG được kéo theo cả API. Ghi log rồi đi tiếp.
            _logger.LogError(ex, "Seed dữ liệu trình diễn thất bại. API vẫn phục vụ bình thường.");
        }
    }
}
