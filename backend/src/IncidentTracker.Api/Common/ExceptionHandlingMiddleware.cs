namespace IncidentTracker.Api.Common;

/// <summary>
/// Error model chuẩn ProblemDetails cho toàn API (mục 6.6).
/// Không lộ stack trace ra client; 5xx chỉ trả correlation id để tra log.
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public ExceptionHandlingMiddleware(RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger, IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            _logger.LogWarning("Từ chối nghiệp vụ {Status} trên {Method} {Path}: {Title}",
                ex.StatusCode, context.Request.Method, context.Request.Path, ex.Title);
            await ProblemDetailsWriter.WriteAsync(
                context, ex.StatusCode, ex.Title, ex.Message, ex.Extensions);
        }
        catch (Exception ex)
        {
            // NFR-SEC-02: log exception nhưng không đưa chi tiết ra response.
            _logger.LogError(ex, "Lỗi chưa xử lý trên {Method} {Path}",
                context.Request.Method, context.Request.Path);
            await ProblemDetailsWriter.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "Lỗi hệ thống",
                _env.IsDevelopment()
                    ? ex.Message
                    : "Đã xảy ra lỗi không mong muốn. Vui lòng đối chiếu correlation id với log hệ thống.");
        }
    }
}
