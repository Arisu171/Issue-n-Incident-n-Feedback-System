namespace IncidentTracker.Api.Common;

/// <summary>
/// Security headers cơ bản (mục 6.7).
///
/// Ba header này không bảo vệ API khỏi client tự viết — <c>curl</c> bỏ qua tất cả. Chúng bảo
/// vệ <b>trình duyệt</b> của người dùng khỏi những cách biến một response JSON vô hại thành
/// bề mặt tấn công: đoán kiểu nội dung, nhúng trong iframe của trang lạ, hoặc rò đường dẫn
/// nội bộ qua header <c>Referer</c>.
///
/// Đặt trong middleware chứ không rải ở controller vì header phải có mặt trên <b>mọi</b>
/// response, kể cả 401, 403 và trang lỗi — đó chính là những response dễ bị bỏ sót nhất.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        // OnStarting để header vẫn còn sau khi ProblemDetailsWriter gọi Response.Clear().
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;

            // Trình duyệt phải tin Content-Type do server khai báo, không được tự đoán.
            headers["X-Content-Type-Options"] = "nosniff";

            // API không có giao diện nên không bao giờ cần được nhúng trong khung của trang khác.
            headers["X-Frame-Options"] = "DENY";

            // Không gửi URL hiện tại sang site khác: đường dẫn API có thể chứa id tài nguyên.
            headers["Referrer-Policy"] = "no-referrer";

            return Task.CompletedTask;
        }, context);

        return _next(context);
    }
}
