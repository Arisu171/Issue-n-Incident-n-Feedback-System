using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace IncidentTracker.Api.Common;

/// <summary>
/// Nơi duy nhất dựng response lỗi của API (mục 6.6, checklist 8.3).
///
/// 401 và 403 do middleware authentication/authorization sinh ra không ném exception nên không
/// đi qua <see cref="ExceptionHandlingMiddleware"/>. Nếu không xử lý riêng, chúng trả body rỗng
/// trong khi các lỗi khác trả ProblemDetails — client sẽ phải parse hai kiểu khác nhau cho cùng
/// một mã trạng thái. Helper này giữ mọi lỗi về đúng một định dạng.
/// </summary>
public static class ProblemDetailsWriter
{
    public const string ContentType = "application/problem+json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ProblemDetails Create(HttpContext context, int status, string title, string detail,
        IDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["correlationId"] = CorrelationIdMiddleware.Current(context);

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        return problem;
    }

    public static async Task WriteAsync(HttpContext context, int status, string title, string detail,
        IDictionary<string, object?>? extensions = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        var problem = Create(context, status, title, detail, extensions);
        var correlationId = CorrelationIdMiddleware.Current(context);

        // Response.Clear() xóa cả header, kể cả X-Correlation-ID mà middleware đã đặt —
        // nên phải gắn lại, nếu không mọi response lỗi sẽ mất đường truy vết ở tầng header.
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        context.Response.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;

        // v3.1 BR-REL-05: 301 của ticket đã transfer mang Location tới URL mới.
        if (extensions is not null && extensions.TryGetValue("location", out var location) && location is string loc)
        {
            context.Response.Headers.Location = loc;
        }

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
    }

    /// <summary>
    /// 401 — token thiếu, sai chữ ký, sai issuer/audience hoặc đã hết hạn.
    /// Không nói rõ token hỏng ở đâu để không giúp kẻ tấn công dò khóa (NFR-SEC-02).
    /// </summary>
    public static Task WriteUnauthorizedAsync(HttpContext context) => WriteAsync(
        context,
        StatusCodes.Status401Unauthorized,
        "Chưa xác thực",
        "Yêu cầu thiếu access token hợp lệ. Đăng nhập lại tại POST /api/auth/login.");

    /// <summary>403 — token hợp lệ nhưng thiếu permission mà endpoint khai báo.</summary>
    public static Task WriteForbiddenAsync(HttpContext context, string? requiredPermission = null)
    {
        var extensions = requiredPermission is null
            ? null
            : new Dictionary<string, object?> { ["requiredPermission"] = requiredPermission };

        var detail = requiredPermission is null
            ? "Tài khoản không có permission cần thiết cho thao tác này."
            : $"Thao tác này yêu cầu permission '{requiredPermission}'.";

        return WriteAsync(context, StatusCodes.Status403Forbidden, "Không đủ quyền", detail, extensions);
    }
}
