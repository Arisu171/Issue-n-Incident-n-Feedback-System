using System.ComponentModel.DataAnnotations;

namespace IncidentTracker.Api.Modules.Tickets.Infrastructure;

/// <summary>Cấu hình module Tickets (Architecture v3.1). Mọi giá trị đều đặt được qua biến môi trường <c>TICKETING__*</c>.</summary>
public sealed class TicketingOptions
{
    public const string SectionName = "Ticketing";

    /// <summary>URL gốc của web, dùng để dựng link trong email/webhook/mention.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>BR-REL-01: thời gian giữ Idempotency-Key.</summary>
    [Range(1, 168)]
    public int IdempotencyTtlHours { get; set; } = 24;

    /// <summary>BR-SEC-03: số request đọc / phút / user.</summary>
    [Range(1, int.MaxValue)]
    public int ReadPerMinute { get; set; } = 300;

    /// <summary>BR-SEC-03: số request ghi / phút / user.</summary>
    [Range(1, int.MaxValue)]
    public int WritePerMinute { get; set; } = 100;

    /// <summary>Mục 6.6: số lời gọi hub / phút / connection.</summary>
    [Range(1, int.MaxValue)]
    public int HubInvocationsPerMinute { get; set; } = 60;

    /// <summary>Số phút giữa hai lần chạy job bảo trì (partition, dọn key). 0 = tắt.</summary>
    [Range(0, 1440)]
    public int MaintenanceIntervalMinutes { get; set; } = 360;

    /// <summary>URL gốc của API (dùng cho URL upload/tải cục bộ).</summary>
    public string PublicApiBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Số message một consumer endpoint xử lý song song. Mỗi lượt consume giữ một connection Npgsql
    /// suốt transaction inbox/outbox, nên tổng <c>(số endpoint × giá trị này)</c> phải nhỏ hơn
    /// <c>Maximum Pool Size</c> của chuỗi kết nối — còn dư chỗ cho request HTTP, health check và job nền.
    /// Mặc định của transport là số CPU cho *mỗi* endpoint: trên máy nhiều nhân, một đợt xả outbox
    /// (ví dụ sau khi seed dữ liệu trình diễn) sẽ chiếm sạch pool và sinh bão timeout.
    /// </summary>
    [Range(1, 256)]
    public int ConsumerConcurrency { get; set; } = 4;

    public RabbitMqOptions RabbitMq { get; set; } = new();
    public WebhookOptions Webhooks { get; set; } = new();
    public SlaOptions Sla { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();

    public sealed class WebhookOptions
    {
        /// <summary>Dev: cho phép http và địa chỉ nội bộ (mặc định false — chặn SSRF, mục 6.7).</summary>
        public bool AllowPrivateNetworks { get; set; }
        /// <summary>Chu kỳ quét delivery đến hạn retry (giây). 0 = tắt.</summary>
        [Range(0, 3600)] public int RetryScanSeconds { get; set; } = 30;
        [Range(1, 120)] public int TimeoutSeconds { get; set; } = 10;
    }

    public sealed class SlaOptions
    {
        /// <summary>UC-16: chu kỳ quét SLA (phút). 0 = tắt.</summary>
        [Range(0, 1440)] public int ScanIntervalMinutes { get; set; } = 1;
    }

    public sealed class StorageOptions
    {
        public string LocalPath { get; set; } = "./storage";
        [Range(1, 1440)] public int PresignMinutes { get; set; } = 15;
        /// <summary>BR-SEC-04: giới hạn kích thước tệp (mặc định 25 MB như GitHub).</summary>
        [Range(1, long.MaxValue)] public long MaxBytes { get; set; } = 25L * 1024 * 1024;
        public S3Options S3 { get; set; } = new();
    }

    public sealed class S3Options
    {
        public string? Bucket { get; set; }
        public string? Region { get; set; }
        public string? ServiceUrl { get; set; }
        public string? AccessKey { get; set; }
        public string? SecretKey { get; set; }
        public bool ForcePathStyle { get; set; } = true;
        public string? PublicBaseUrl { get; set; }
        public bool Enabled => !string.IsNullOrWhiteSpace(Bucket);
    }

    public sealed class RabbitMqOptions
    {
        /// <summary>Rỗng = dùng transport in-memory (sai lệch có chủ đích cho môi trường dev, xem modify_00).</summary>
        public string? Host { get; set; }
        public string VirtualHost { get; set; } = "/";
        public string Username { get; set; } = "guest";
        public string Password { get; set; } = "guest";
        public bool Enabled => !string.IsNullOrWhiteSpace(Host);
    }
}
