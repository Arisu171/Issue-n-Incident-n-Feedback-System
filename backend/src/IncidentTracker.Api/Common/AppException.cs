namespace IncidentTracker.Api.Common;

/// <summary>
/// Lỗi nghiệp vụ có mã HTTP xác định. Service ném lỗi này, controller không dịch thủ công;
/// ProblemDetails được sinh tập trung ở <see cref="ProblemDetailsFactoryExtensions"/>.
/// </summary>
public class AppException : Exception
{
    public AppException(int statusCode, string title, string detail,
        IDictionary<string, object?>? extensions = null) : base(detail)
    {
        StatusCode = statusCode;
        Title = title;
        Extensions = extensions ?? new Dictionary<string, object?>();
    }

    public int StatusCode { get; }
    public string Title { get; }
    public IDictionary<string, object?> Extensions { get; }

    public static AppException NotFound(string detail)
        => new(StatusCodes.Status404NotFound, "Không tìm thấy tài nguyên", detail);

    public static AppException Conflict(string detail, IDictionary<string, object?>? extensions = null)
        => new(StatusCodes.Status409Conflict, "Xung đột trạng thái", detail, extensions);

    public static AppException BadRequest(string detail)
        => new(StatusCodes.Status400BadRequest, "Dữ liệu không hợp lệ", detail);

    public static AppException Forbidden(string detail)
        => new(StatusCodes.Status403Forbidden, "Không đủ quyền", detail);
}
