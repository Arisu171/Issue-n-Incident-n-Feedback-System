using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Incidents;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Host thật + PostgreSQL thật. Không dùng InMemory provider vì phần lớn ràng buộc của thiết kế
/// (check constraint, partial index, FOR UPDATE, transaction) chỉ tồn tại ở tầng PostgreSQL.
///
/// Chuỗi kết nối lấy từ biến môi trường <c>TEST_DB_CONNECTION</c>; mặc định trỏ tới service
/// <c>db</c> của docker compose. Mỗi lần chạy dùng một database riêng rồi hủy sau khi xong.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SigningKey = "test-signing-key-must-be-at-least-32-characters-long";

    private readonly string _databaseName = $"it_test_{Guid.NewGuid():N}";
    private string _adminConnectionString = string.Empty;
    private string _testConnectionString = string.Empty;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Bật/tắt fault injection cho TC-NFR-02 ngay giữa transaction chuyển trạng thái.</summary>
    public ControllableFaultHook FaultHook { get; } = new();

    /// <summary>Log đã ghi, dùng kiểm chứng FR-011, NFR-AUD-01 và NFR-SEC-02.</summary>
    public LogCapture Logs { get; } = new();

    /// <summary>Receiver webhook giả (modify_08).</summary>
    public FakeWebhookReceiver WebhookReceiver { get; } = new();

    public static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("TEST_DB_CONNECTION")
        ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

    public async Task InitializeAsync()
    {
        _adminConnectionString = BaseConnectionString;

        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = _databaseName };
        _testConnectionString = builder.ToString();

        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Phải dùng UseSetting chứ không phải ConfigureAppConfiguration: Program.cs đọc
        // builder.Configuration ngay trong Main, trước khi các callback cấu hình hoãn lại
        // của WebApplicationFactory kịp chạy. UseSetting ghi thẳng vào host configuration.
        builder.UseSetting("ConnectionStrings:Default", _testConnectionString);
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting("Jwt:Issuer", "gitissues-api");
        builder.UseSetting("Jwt:Audience", "gitissues-web");
        builder.UseSetting("Jwt:ExpiryMinutes", "15");
        // Môi trường test không phải Development nên Swagger mặc định tắt; bật tường minh để
        // kiểm chứng được đặc tả. OpenApiTests tự ghi đè lại giá trị này khi cần thử mặc định.
        builder.UseSetting("Swagger:Enabled", "true");
        // Integration test không dựng container web, nên mặc định trỏ check "web" vào một cổng
        // không ai nghe: trạng thái mặc định của nó là Unhealthy. Test nào cần nhánh healthy thì
        // tự dựng StubWebService rồi ghi đè lại. Việc ba container thật cùng healthy do bước
        // docker compose trong CI đảm nhiệm.
        builder.UseSetting("HealthChecks:WebUrl", "http://127.0.0.1:59997/healthz");
        builder.UseSetting("HealthChecks:TimeoutSeconds", "2");
        builder.UseSetting("Seed:ApplyMigrations", "true");
        builder.UseSetting("Seed:AdminEmail", "admin@test.local");
        builder.UseSetting("Seed:AdminPassword", "Admin#12345");
        // Tài khoản `system` tách khỏi admin. Ở production danh tính này chỉ nằm trong biến môi
        // trường; test thì phải tự dựng bối cảnh nên đặt tường minh, giống admin@test.local.
        builder.UseSetting("Seed:SystemEmail", "system@test.local");
        builder.UseSetting("Seed:SystemPassword", "System#12345");
        // Rate limit cao để không làm hỏng các test đăng nhập liên tiếp.
        builder.UseSetting("RateLimit:LoginPerMinute", "100000");
        builder.UseSetting("RateLimit:RegisterPerHour", "100000");
        // Module Tickets (v3.1): rate limit cao, job bảo trì tắt để log test gọn.
        builder.UseSetting("Ticketing:ReadPerMinute", "1000000");
        builder.UseSetting("Ticketing:WritePerMinute", "1000000");
        builder.UseSetting("Ticketing:MaintenanceIntervalMinutes", "0");

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        // Webhook: không mở cổng thật — receiver giả ghi lại request để kiểm HMAC/payload.
        builder.UseSetting("Ticketing:Webhooks:AllowPrivateNetworks", "true");
        builder.UseSetting("Ticketing:Webhooks:RetryScanSeconds", "0");
        builder.UseSetting("Ticketing:Sla:ScanIntervalMinutes", "0");
        builder.UseSetting("Ticketing:Storage:LocalPath", Path.Combine(Path.GetTempPath(), "it-storage-" + _databaseName));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITransactionFaultHook>();
            services.AddSingleton<ITransactionFaultHook>(FaultHook);
            services.RemoveAll<IncidentTracker.Api.Modules.Tickets.Webhooks.IWebhookHttpClient>();
            services.AddSingleton<IncidentTracker.Api.Modules.Tickets.Webhooks.IWebhookHttpClient>(WebhookReceiver);
        });
    }

    public async Task<HttpClient> CreateClientAsAsync(string email, string password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { identifier = email, password }, Json);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<LoginPayload>(Json);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", payload!.AccessToken);
        return client;
    }

    /// <summary>Token thô cho SignalR (JWT qua query string access_token — mục 6.6).</summary>
    public async Task<string> LoginTokenAsync(string email, string password)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { identifier = email, password }, Json);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<LoginPayload>(Json);
        return payload!.AccessToken;
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_testConnectionString).Options;
        return new AppDbContext(options);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        catch (Exception)
        {
            // MassTransit/SignalR có thể ném khi dừng host giữa lúc consumer còn chạy — chỉ là hạ tầng test.
        }
        NpgsqlConnection.ClearAllPools();

        // Bus outbox/consumer của MassTransit có thể còn giữ kết nối vài trăm ms sau khi host dừng → thử lại.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_adminConnectionString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)", conn);
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (Exception) when (attempt < 5)
            {
                NpgsqlConnection.ClearAllPools();
                await Task.Delay(1000);
            }
        }
    }

    public sealed record LoginPayload(string AccessToken, Guid UserId, string[] Permissions);
}

/// <summary>Ghi lại mọi request webhook; trả mã trạng thái cấu hình được theo URL.</summary>
public sealed class FakeWebhookReceiver : IncidentTracker.Api.Modules.Tickets.Webhooks.IWebhookHttpClient
{
    public sealed record Captured(string Url, IReadOnlyDictionary<string, string> Headers, string Body, DateTimeOffset At);

    private readonly System.Collections.Concurrent.ConcurrentQueue<Captured> _requests = new();
    public IReadOnlyList<Captured> Requests => _requests.ToArray();
    public Func<string, int> StatusFor { get; set; } = _ => 200;

    public Task<IncidentTracker.Api.Modules.Tickets.Webhooks.WebhookHttpResult> PostAsync(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct)
    {
        _requests.Enqueue(new Captured(url, headers, body, DateTimeOffset.UtcNow));
        var status = StatusFor(url);
        return Task.FromResult(new IncidentTracker.Api.Modules.Tickets.Webhooks.WebhookHttpResult(status, status >= 200 && status < 300 ? "ok" : "error", null));
    }
}

/// <summary>Fault hook điều khiển được, phục vụ TC-NFR-02.</summary>
public sealed class ControllableFaultHook : ITransactionFaultHook
{
    public bool ShouldThrow { get; set; }

    public Task AfterHistoryAppendedAsync(Guid incidentId, CancellationToken ct)
        => ShouldThrow
            ? throw new InvalidOperationException("Fault injection: mô phỏng lỗi DB sau khi ghi lịch sử.")
            : Task.CompletedTask;
}
