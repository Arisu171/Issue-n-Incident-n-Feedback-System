using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Tickets.Storage;

/// <summary>
/// BR-EV-05 / GOAL-PERF-02: presigned PUT lên S3/MinIO; policy ký ràng buộc Content-Type và Content-Length
/// (BR-SEC-04). Bật khi cấu hình <c>Ticketing:Storage:S3:Bucket</c>.
/// </summary>
public sealed class S3StorageProvider : IStorageProvider, IDisposable
{
    private readonly TicketingOptions.S3Options _s3;
    private readonly IAmazonS3 _client;
    private readonly TimeProvider _clock;

    public S3StorageProvider(IOptions<TicketingOptions> options, TimeProvider clock)
    {
        _s3 = options.Value.Storage.S3;
        _clock = clock;
        var config = new AmazonS3Config { ForcePathStyle = _s3.ForcePathStyle };
        if (!string.IsNullOrWhiteSpace(_s3.ServiceUrl)) config.ServiceURL = _s3.ServiceUrl;
        else if (!string.IsNullOrWhiteSpace(_s3.Region)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(_s3.Region);
        _client = string.IsNullOrWhiteSpace(_s3.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(_s3.AccessKey, _s3.SecretKey, config);
    }

    public string Name => "s3";

    public Task<PresignedUpload> PresignUploadAsync(string key, string contentType, long contentLength, CancellationToken ct)
    {
        var expires = _clock.GetUtcNow().AddMinutes(options().PresignMinutes);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _s3.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = expires.UtcDateTime,
            ContentType = contentType
        };
        request.Headers["Content-Length"] = contentLength.ToString();
        var url = _client.GetPreSignedURL(request);
        return Task.FromResult(new PresignedUpload(url, "PUT",
            new Dictionary<string, string> { ["Content-Type"] = contentType, ["Content-Length"] = contentLength.ToString() },
            key, PublicUrl(key), expires, contentLength));
    }

    public Task<string> PublicUrlAsync(string key, CancellationToken ct)
    {
        // Link tải cũng là presigned GET ngắn hạn: bucket giữ private (BR-SEC-04).
        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _s3.Bucket, Key = key, Verb = HttpVerb.GET, Expires = _clock.GetUtcNow().AddMinutes(options().PresignMinutes).UtcDateTime
        });
        return Task.FromResult(url);
    }

    private string PublicUrl(string key) => string.IsNullOrWhiteSpace(_s3.PublicBaseUrl) ? $"s3://{_s3.Bucket}/{key}" : $"{_s3.PublicBaseUrl.TrimEnd('/')}/{key}";

    private TicketingOptions.StorageOptions options() => new() { PresignMinutes = 15 };

    public void Dispose() => _client.Dispose();
}
