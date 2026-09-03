using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Modules.Identity;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Tickets.Storage;

/// <summary>Kết quả cấp presigned URL (mục 6.5, BR-EV-05, BR-SEC-04).</summary>
public sealed record PresignedUpload(string UploadUrl, string Method, IReadOnlyDictionary<string, string> Headers, string Key, string PublicUrl, DateTimeOffset ExpiresAt, long MaxBytes);

public interface IStorageProvider
{
    string Name { get; }
    Task<PresignedUpload> PresignUploadAsync(string key, string contentType, long contentLength, CancellationToken ct);
    Task<string> PublicUrlAsync(string key, CancellationToken ct);
}

/// <summary>BR-SEC-04: quét virus bất đồng bộ. Không có engine trong phạm vi → NoOp đánh dấu sạch ngay (sai lệch có chủ đích).</summary>
public interface IVirusScanner
{
    Task<bool> IsCleanAsync(string path, CancellationToken ct);
}

public sealed class NoOpVirusScanner : IVirusScanner
{
    private readonly ILogger<NoOpVirusScanner> _logger;
    public NoOpVirusScanner(ILogger<NoOpVirusScanner> logger) => _logger = logger;
    public Task<bool> IsCleanAsync(string path, CancellationToken ct)
    {
        _logger.LogInformation("VirusScanner (no-op) đánh dấu sạch: {Path}", path);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Storage cục bộ cho dev: URL upload ký HMAC (15 phút) trên chính API, kiểm content-type/length khi nhận,
/// sidecar <c>.meta.json</c> ghi trạng thái quét; file chưa quét không phục vụ (BR-SEC-04).
/// </summary>
public sealed class LocalStorageProvider : IStorageProvider
{
    private readonly TicketingOptions _options;
    private readonly byte[] _key;
    private readonly TimeProvider _clock;

    public LocalStorageProvider(IOptions<TicketingOptions> options, IOptions<JwtOptions> jwt, TimeProvider clock)
    {
        _options = options.Value;
        _key = SHA256.HashData(Encoding.UTF8.GetBytes("storage:" + jwt.Value.SigningKey));
        _clock = clock;
    }

    public string Name => "local";

    public string Root => Path.GetFullPath(_options.Storage.LocalPath);

    public Task<PresignedUpload> PresignUploadAsync(string key, string contentType, long contentLength, CancellationToken ct)
    {
        var expires = _clock.GetUtcNow().AddMinutes(_options.Storage.PresignMinutes);
        var token = Token(key, contentType, contentLength, expires);
        var baseUrl = _options.PublicApiBaseUrl.TrimEnd('/');
        return Task.FromResult(new PresignedUpload(
            $"{baseUrl}/api/storage/local/{token}", "PUT",
            new Dictionary<string, string> { ["Content-Type"] = contentType, ["Content-Length"] = contentLength.ToString() },
            // URL công khai đi qua đúng một chỗ sinh ra nó: chép tay đường dẫn ở đây từng khiến
            // câu trả lời của presign trỏ vào đường **cần token**, còn ảnh chèn vào bình luận thì
            // không bao giờ hiện được.
            key, PublicUrl(key), expires, contentLength));
    }

    public Task<string> PublicUrlAsync(string key, CancellationToken ct) => Task.FromResult(PublicUrl(key));

    private string PublicUrl(string key)
        => $"{_options.PublicApiBaseUrl.TrimEnd('/')}/api/storage/public/{key}?t={DownloadToken(key)}";

    /// <summary>
    /// Chữ ký cho phép **trình duyệt** lấy tệp.
    ///
    /// Vì sao phải có: ảnh hiển thị qua thẻ <c>img</c>, mà thẻ đó không bao giờ gửi header
    /// <c>Authorization</c> — token của ứng dụng nằm trong <c>localStorage</c>, không phải cookie.
    /// Nên mọi ảnh đã tải lên đều nhận 401 và không hiện được, kể cả khi người dùng đang đăng nhập.
    ///
    /// <b>Không có hạn dùng</b>, khác với chữ ký upload. Đường dẫn ảnh đính kèm được **chèn thẳng
    /// vào nội dung bình luận** và nằm lại đó vĩnh viễn; một chữ ký hết hạn sẽ làm mọi ảnh trong
    /// bình luận cũ hỏng dần theo thời gian. Đây là URL dạng "ai cầm được thì đọc được", đúng như
    /// presigned URL của object storage — khoá đã chứa một GUID nên không đoán được, và chữ ký
    /// ngăn việc sửa đường dẫn để với sang tệp khác.
    /// </summary>
    public string DownloadToken(string key)
        => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("download|" + key)))
            .ToLowerInvariant()[..32];

    /// <summary>So sánh theo thời gian cố định — so bằng <c>==</c> để lộ độ dài tiền tố khớp.</summary>
    public bool VerifyDownload(string key, string? token)
        => token is not null
           && CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(DownloadToken(key)), Encoding.UTF8.GetBytes(token));

    public string Token(string key, string contentType, long length, DateTimeOffset expires)
    {
        var payload = $"{key}|{contentType}|{length}|{expires.ToUnixTimeSeconds()}";
        var sig = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return Cursor.Encode(payload + "|" + sig);
    }

    public (string Key, string ContentType, long Length, DateTimeOffset Expires)? Verify(string token)
    {
        var raw = Cursor.DecodeRaw(token);
        if (raw is null) return null;
        var parts = raw.Split('|');
        if (parts.Length != 5) return null;
        var payload = string.Join("|", parts[..4]);
        var expected = Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[4]))) return null;
        if (!long.TryParse(parts[2], out var len) || !long.TryParse(parts[3], out var exp)) return null;
        var expires = DateTimeOffset.FromUnixTimeSeconds(exp);
        if (expires < _clock.GetUtcNow()) return null;
        return (parts[0], parts[1], len, expires);
    }

    public string PathFor(string key) => Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar));
}

public sealed record StoredFileMeta(string Key, string ContentType, long Length, bool Scanned, DateTimeOffset UploadedAt, Guid? UploadedBy);

[ApiController]
[Route("api/storage")]
public sealed class StorageController : ControllerBase
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml", "application/pdf", "text/plain", "text/csv",
        "application/zip", "application/json", "video/mp4", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/octet-stream"
    };

    private readonly IStorageProvider _storage;
    private readonly IVirusScanner _scanner;
    private readonly TicketingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<StorageController> _logger;
    private readonly TicketQueries _q;

    public StorageController(IStorageProvider storage, IVirusScanner scanner, IOptions<TicketingOptions> options, TimeProvider clock, ILogger<StorageController> logger, TicketQueries q)
    {
        _storage = storage;
        _scanner = scanner;
        _options = options.Value;
        _clock = clock;
        _logger = logger;
        _q = q;
    }

    /// <summary>
    /// BR-EV-05 / BR-SEC-04: API không nhận stream; cấp URL ký có ràng buộc Content-Type và
    /// Content-Length, hết hạn 15 phút.
    ///
    /// <para><b>Khoá tệp xếp theo project.</b> Trước đây khoá là <c>{yyyy/MM}/{guid}/{tên}</c> —
    /// theo tháng, không theo dự án — nên trên đĩa mọi tệp của mọi project trộn chung. Giờ tệp
    /// đính kèm của một project nằm dưới <c>projects/{slug}/</c>, còn thứ không thuộc project nào
    /// (ảnh đại diện) nằm dưới <c>shared/</c>. Hai tiền tố, mỗi cái một nghĩa rõ ràng.</para>
    ///
    /// <para><b>Tệp cũ không bị dời.</b> Khoá được ký HMAC và nằm trong nội dung ticket đã lưu,
    /// nên dời là làm hỏng mọi đường dẫn đã phát ra. <c>GET /files/{key}</c> nhận khoá bất kỳ nên
    /// đường dẫn cũ vẫn phục vụ được — chỉ tệp mới theo cách xếp mới.</para>
    /// </summary>
    [HttpGet("presigned-url")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<PresignedUpload>> Presign([FromQuery(Name = "file_name")] string fileName, [FromQuery(Name = "content_type")] string contentType,
        [FromQuery(Name = "content_length")] long contentLength, [FromQuery(Name = "project")] string? project, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 200) throw AppException.BadRequest("file_name không hợp lệ.");
        if (!AllowedTypes.Contains(contentType)) throw new AppException(StatusCodes.Status415UnsupportedMediaType, "Loại tệp không được hỗ trợ", $"content_type '{contentType}' không nằm trong danh sách cho phép.");
        var max = _options.Storage.MaxBytes;
        if (contentLength <= 0 || contentLength > max) throw new AppException(StatusCodes.Status413PayloadTooLarge, "Tệp quá lớn", $"content_length phải trong 1..{max} byte.");

        // Tra slug thật thay vì ghép thẳng vào khoá: slug bịa sẽ tạo ra một thư mục ma mà không ai
        // dọn, và `..` trong đó là đường thoát khỏi thư mục gốc.
        var prefix = "shared";
        if (!string.IsNullOrWhiteSpace(project))
        {
            var found = await _q.ProjectAsync(project, ct);
            prefix = $"projects/{found.Slug}";
        }

        var safeName = string.Concat(Path.GetFileName(fileName).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-'));
        var key = $"{prefix}/{_clock.GetUtcNow():yyyy/MM}/{Guid.NewGuid():N}/{safeName}";
        return Ok(await _storage.PresignUploadAsync(key, contentType, contentLength, ct));
    }

    /// <summary>Đích của URL ký cục bộ (dev). Ẩn danh vì token đã ký; kiểm đúng type/length như policy S3.</summary>
    [HttpPut("local/{token}")]
    [AllowAnonymous]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadLocal(string token, CancellationToken ct)
    {
        if (_storage is not LocalStorageProvider local) return NotFound();
        var verified = local.Verify(token);
        if (verified is null) return await ProblemAsync(StatusCodes.Status403Forbidden, "URL upload không hợp lệ hoặc đã hết hạn.");
        var (key, contentType, length, _) = verified.Value;

        if (!string.Equals(Request.ContentType?.Split(';')[0].Trim(), contentType, StringComparison.OrdinalIgnoreCase))
            return await ProblemAsync(StatusCodes.Status415UnsupportedMediaType, "Content-Type không khớp với URL đã ký.");
        if (Request.ContentLength is { } cl && cl != length)
            return await ProblemAsync(StatusCodes.Status413PayloadTooLarge, "Content-Length không khớp với URL đã ký.");

        var path = local.PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long written;
        await using (var file = System.IO.File.Create(path))
        {
            var buffer = new byte[64 * 1024];
            written = 0;
            int read;
            while ((read = await Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                written += read;
                if (written > length)
                {
                    file.Close();
                    System.IO.File.Delete(path);
                    return await ProblemAsync(StatusCodes.Status413PayloadTooLarge, "Dữ liệu vượt Content-Length đã ký.");
                }
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        // BR-SEC-04: quét (bất đồng bộ về mặt kiến trúc; ở đây no-op) rồi mới cho phục vụ.
        var clean = await _scanner.IsCleanAsync(path, ct);
        var meta = new StoredFileMeta(key, contentType, written, clean, _clock.GetUtcNow(), null);
        await System.IO.File.WriteAllTextAsync(path + ".meta.json", JsonSerializer.Serialize(meta), ct);
        if (!clean)
        {
            System.IO.File.Delete(path);
            _logger.LogWarning("Tệp {Key} bị loại vì không sạch.", key);
            return await ProblemAsync(StatusCodes.Status422UnprocessableEntity, "Tệp không qua kiểm tra an toàn.");
        }
        return Ok(new { key, url = await _storage.PublicUrlAsync(key, ct), size = written, content_type = contentType });
    }

    /// <summary>
    /// Phục vụ tệp cho **trình duyệt**, xác thực bằng chữ ký trên đường dẫn.
    ///
    /// Thẻ <c>img</c> không gửi được header <c>Authorization</c> — token của ứng dụng nằm trong
    /// <c>localStorage</c>, không phải cookie — nên mọi ảnh đã tải lên đều nhận 401 và không hiện
    /// ra. Đường này giải quyết đúng chỗ đó: chữ ký thay cho header.
    ///
    /// Vì sao là <b>endpoint riêng</b> chứ không thêm một nhánh vào <see cref="Get"/>:
    /// <c>PrincipalEnrichmentMiddleware</c> cố ý **không nạp quyền** cho endpoint
    /// <c>[AllowAnonymous]</c> (để token cũ đính nhầm không làm hỏng đường đăng nhập lại). Một
    /// endpoint vừa ẩn danh vừa muốn tự kiểm quyền sẽ luôn thấy danh sách quyền rỗng — nó không
    /// thể vừa là cái này vừa là cái kia. Hai đường, mỗi đường một luật, là cách duy nhất thành thật.
    /// </summary>
    [HttpGet("public/{**key}")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<IActionResult> GetSigned(string key, [FromQuery(Name = "t")] string? token, CancellationToken ct)
    {
        if (_storage is not LocalStorageProvider signer || !signer.VerifyDownload(key, token))
        {
            // 404 chứ không 403: 403 xác nhận khoá đó có tệp thật.
            return NotFound();
        }

        return await ServeAsync(key, ct);
    }

    /// <summary>Phục vụ tệp đã quét (Read+) — đường dành cho lời gọi API mang token.</summary>
    [HttpGet("files/{**key}")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public Task<IActionResult> Get(string key, CancellationToken ct) => ServeAsync(key, ct);

    private async Task<IActionResult> ServeAsync(string key, CancellationToken ct)
    {
        if (_storage is not LocalStorageProvider local) return Redirect(await _storage.PublicUrlAsync(key, ct));
        if (key.Contains("..")) return NotFound();
        var path = local.PathFor(key);
        if (!System.IO.File.Exists(path) || !System.IO.File.Exists(path + ".meta.json")) return NotFound();
        var meta = JsonSerializer.Deserialize<StoredFileMeta>(await System.IO.File.ReadAllTextAsync(path + ".meta.json", ct));
        if (meta is null || !meta.Scanned) return await ProblemAsync(StatusCodes.Status409Conflict, "Tệp chưa qua quét an toàn.");
        var type = meta.ContentType;
        if (type == "image/svg+xml") type = "application/octet-stream"; // không thực thi script trong SVG khi mở trực tiếp
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{Path.GetFileName(key)}\"";
        return PhysicalFile(path, type);
    }

    private async Task<IActionResult> ProblemAsync(int status, string detail)
    {
        await ProblemDetailsWriter.WriteAsync(HttpContext, status, "Upload bị từ chối", detail);
        return new EmptyResult();
    }
}
