using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Tickets.Infrastructure;

/// <summary>Đánh dấu endpoint <b>bắt buộc</b> có header <c>Idempotency-Key</c> (BR-REL-01: POST tạo ticket/comment).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireIdempotencyKeyAttribute : Attribute;

/// <summary>
/// BR-REL-01 (Architecture v3.1): mọi request ghi có thể kèm <c>Idempotency-Key</c> (UUID do client
/// sinh). Cùng key + cùng nội dung → trả lại đúng response đã lưu (header <c>Idempotent-Replayed</c>);
/// cùng key nhưng nội dung khác → 422. Key lưu ở bảng <c>idempotency_keys</c> — tách khỏi bảng
/// partition — TTL 24h; job bảo trì dọn key hết hạn.
///
/// <para>Chạy sau Authentication vì key gắn với user; endpoint ẩn danh không đi qua đây.</para>
/// </summary>
public sealed partial class IdempotencyMiddleware
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post, HttpMethods.Patch, HttpMethods.Put, HttpMethods.Delete
    };

    private readonly RequestDelegate _next;

    public IdempotencyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AppDbContext db, IOptions<TicketingOptions> options,
        TimeProvider clock, ILogger<IdempotencyMiddleware> logger)
    {
        if (!WriteMethods.Contains(context.Request.Method) || context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        var required = endpoint?.Metadata.GetMetadata<RequireIdempotencyKeyAttribute>() is not null;
        var key = context.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(key))
        {
            if (required)
            {
                await ProblemDetailsWriter.WriteAsync(context, StatusCodes.Status400BadRequest,
                    "Thiếu Idempotency-Key",
                    $"Endpoint này yêu cầu header '{HeaderName}' (UUID do client sinh) để chống tạo trùng khi retry (BR-REL-01).");
                return;
            }

            await _next(context);
            return;
        }

        if (!KeyRegex().IsMatch(key))
        {
            await ProblemDetailsWriter.WriteAsync(context, StatusCodes.Status400BadRequest,
                "Idempotency-Key không hợp lệ", "Key chỉ gồm chữ, số, '-' và '_', dài 1–64 ký tự.");
            return;
        }

        Guid userId;
        try
        {
            userId = context.User.GetUserId();
        }
        catch (InvalidOperationException)
        {
            await _next(context);
            return;
        }

        context.Request.EnableBuffering();
        var hash = await ComputeHashAsync(context.Request, context.RequestAborted);
        var now = clock.GetUtcNow();

        var existing = await db.IdempotencyKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Key == key && k.UserId == userId, context.RequestAborted);

        if (existing is not null && existing.ExpiresAt > now)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                await ProblemDetailsWriter.WriteAsync(context, StatusCodes.Status422UnprocessableEntity,
                    "Idempotency-Key đã dùng cho request khác",
                    "Cùng một Idempotency-Key nhưng nội dung request khác với lần đầu. Sinh key mới cho request mới.",
                    new Dictionary<string, object?> { ["idempotencyKey"] = key });
                return;
            }

            logger.LogInformation("Idempotency replay key={Key} user={UserId} status={Status}", key, userId, existing.ResponseStatus);
            context.Response.StatusCode = existing.ResponseStatus;
            context.Response.Headers[ReplayedHeader] = "true";
            if (!string.IsNullOrEmpty(existing.ResponseHeaders))
            {
                foreach (var (name, value) in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(existing.ResponseHeaders) ?? new())
                {
                    context.Response.Headers[name] = value;
                }
            }
            if (existing.ResponseBody is not null)
            {
                context.Response.ContentType = existing.ResponseContentType ?? "application/json";
                await context.Response.WriteAsync(existing.ResponseBody, context.RequestAborted);
            }
            return;
        }

        // Thực thi thật, ghi lại response để lần retry sau replay được.
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);

            buffer.Position = 0;
            var body = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync(context.RequestAborted);

            if (context.Response.StatusCode < 500)
            {
                var headers = new Dictionary<string, string>();
                foreach (var name in new[] { "ETag", "Location" })
                {
                    if (context.Response.Headers.TryGetValue(name, out var v))
                    {
                        headers[name] = v.ToString();
                    }
                }

                db.IdempotencyKeys.Add(new IdempotencyKey
                {
                    Key = key,
                    UserId = userId,
                    RequestHash = hash,
                    ResponseStatus = context.Response.StatusCode,
                    ResponseBody = body.Length == 0 ? null : body,
                    ResponseContentType = context.Response.ContentType,
                    ResponseHeaders = System.Text.Json.JsonSerializer.Serialize(headers),
                    CreatedAt = now,
                    ExpiresAt = now.AddHours(options.Value.IdempotencyTtlHours)
                });

                // Hai request cùng key chạy song song: cái đến sau thua unique PK, bỏ qua — cả hai
                // đều đã có response thật của mình.
                await db.SaveIgnoringDuplicateAsync(CancellationToken.None);
            }

            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, CancellationToken.None);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static async Task<string> ComputeHashAsync(HttpRequest request, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        var prefix = Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}\n{request.QueryString}\n");
        sha.TransformBlock(prefix, 0, prefix.Length, null, 0);

        request.Body.Position = 0;
        var bodyBuffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(bodyBuffer, ct)) > 0)
        {
            sha.TransformBlock(bodyBuffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        request.Body.Position = 0;

        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex KeyRegex();
}
