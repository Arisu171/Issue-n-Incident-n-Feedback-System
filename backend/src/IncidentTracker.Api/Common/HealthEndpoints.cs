using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IncidentTracker.Api.Common;

/// <summary>Cấu hình health check (mục 6.9, NFR-PORT-01).</summary>
public sealed class HealthCheckOptions
{
    public const string SectionName = "HealthChecks";

    /// <summary>
    /// Địa chỉ health của service web. Trong compose là tên service nội bộ
    /// <c>http://web:3000/healthz</c>; khi chạy trực tiếp thì là localhost.
    /// </summary>
    public string WebUrl { get; set; } = "http://localhost:3000/healthz";

    /// <summary>Thời gian chờ tối đa khi hỏi web, giữ ngắn để endpoint tổng hợp không treo.</summary>
    public int TimeoutSeconds { get; set; } = 5;
}

public static class HealthEndpoints
{
    /// <summary>Chỉ những check quyết định container api có sẵn sàng nhận request hay không.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Toàn bộ hệ thống, gồm cả service web nằm ngoài tiến trình này.</summary>
    public const string SystemTag = "system";

    /// <summary>
    /// Hai endpoint có mục đích khác nhau, và việc tách chúng ra là bắt buộc chứ không phải
    /// cho đẹp:
    ///
    /// <c>/api/health</c> chỉ soi bản thân api và database. Đây là endpoint mà docker compose
    /// dùng làm healthcheck. Nếu nhét thêm check tới web vào đây sẽ tạo khóa chết: compose
    /// khai báo web <c>depends_on api healthy</c>, mà api lại chờ web trả lời — không service
    /// nào lên được.
    ///
    /// <c>/api/health/system</c> soi cả ba service để một lời gọi từ Postman là biết toàn cảnh.
    /// Endpoint này không được dùng làm healthcheck của container.
    /// </summary>
    public static void MapAppHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/api/health", new HealthCheckOptions_(ReadyTag)).AllowAnonymous();
        app.MapHealthChecks("/api/health/system", new HealthCheckOptions_(SystemTag)).AllowAnonymous();
    }

    /// <summary>Bọc để hai lời gọi ở trên đọc gọn; tên có gạch dưới tránh đụng lớp cấu hình cùng tên.</summary>
    private sealed class HealthCheckOptions_ : Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        public HealthCheckOptions_(string tag)
        {
            Predicate = registration => registration.Tags.Contains(tag);
            ResponseWriter = WriteResponseAsync;

            // Hệ thống giám sát chỉ đọc mã trạng thái nên phải phân biệt được ba mức.
            ResultStatusCodes = new Dictionary<HealthStatus, int>
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            };
        }
    }

    private static async Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            correlationId = CorrelationIdMiddleware.Current(context),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 2),
                description = entry.Value.Description,
                // Không trả exception ra ngoài: thông điệp của Npgsql có thể chứa chuỗi kết nối.
                error = entry.Value.Exception is null ? null : "Xem log hệ thống theo correlationId."
            })
        });
    }
}
