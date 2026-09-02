using System.ComponentModel.DataAnnotations;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace IncidentTracker.Api.Observability;

/// <summary>Cấu hình mục 6.9. Mọi ngưỡng đều chỉnh được bằng biến môi trường.</summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>Phơi <c>/metrics</c> theo định dạng Prometheus.</summary>
    public bool EnablePrometheus { get; set; } = true;

    /// <summary>
    /// Tỷ lệ lấy mẫu trace. Mục 6.9 đề xuất 10% và đánh dấu [XÁC NHẬN]; nhóm giữ giá trị này
    /// làm mặc định và ghi lại thành cấu hình thay vì hằng số trong mã.
    /// </summary>
    [Range(0d, 1d)]
    public double TraceSampleRatio { get; set; } = 0.1;

    // Không có OtlpEndpoint: gói OpenTelemetry.Exporter.OpenTelemetryProtocol đã bị gỡ.
    //
    // Nó kéo theo cả chuỗi gRPC và protobuf mang advisory, trong khi dự án không dựng collector
    // nào nên đường xuất đó chưa từng được dùng. Span vẫn được tạo và lấy mẫu bình thường;
    // cần xuất đi thật thì thêm lại gói rồi gọi tracing.AddOtlpExporter:
    //
    //   dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
    //
    // Lưu ý bản 1.13 trở lên đòi Microsoft.Extensions.* 9.0 nên không chạy trên runtime .NET 8.
}

public static class ObservabilitySetup
{
    public static IServiceCollection AddAppObservability(
        this IServiceCollection services, IConfiguration configuration, string serviceVersion)
    {
        services.Configure<ObservabilityOptions>(configuration.GetSection(ObservabilityOptions.SectionName));

        var options = configuration.GetSection(ObservabilityOptions.SectionName)
            .Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        services.AddSingleton<AppMetrics>();

        var builder = services.AddOpenTelemetry().ConfigureResource(r => r
            .AddService("incident-tracker-api", serviceVersion: serviceVersion));

        builder.WithMetrics(metrics =>
        {
            metrics
                .AddAspNetCoreInstrumentation()   // request rate, tỷ lệ 4xx/5xx, histogram latency cho p95
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(AppMetrics.MeterName);

            if (options.EnablePrometheus)
            {
                metrics.AddPrometheusExporter();
            }
        });

        builder.WithTracing(tracing =>
        {
            tracing
                .SetSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio))
                .AddAspNetCoreInstrumentation(o =>
                {
                    // Không gắn health check và /metrics vào trace: chúng chạy liên tục và
                    // sẽ nhấn chìm các span nghiệp vụ thật.
                    o.Filter = context =>
                        !context.Request.Path.StartsWithSegments("/api/health")
                        && !context.Request.Path.StartsWithSegments("/metrics");
                })
                .AddHttpClientInstrumentation()
                .AddSource(AppMetrics.ActivitySourceName);
        });

        return services;
    }
}
