namespace IncidentTracker.Api.Common;

/// <summary>
/// NFR-AUD-01 — gắn correlation id cho mọi request để truy vết log.
/// Nhận lại giá trị client gửi nếu hợp lệ, ngược lại sinh mới; luôn echo qua response header.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    private const int MaxLength = 64;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = IsSafe(incoming) ? incoming! : Guid.NewGuid().ToString("N");

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(context);
        }
    }

    // Chặn header injection / log forging từ client không tin cậy.
    private static bool IsSafe(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= MaxLength
           && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    public static string Current(HttpContext context)
        => context.Items.TryGetValue(HeaderName, out var v) && v is string s ? s : string.Empty;
}
