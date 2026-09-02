using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Tickets.Webhooks;

public static class WebhookEvents
{
    public static readonly string[] All = { "issues", "issue_comment", "sub_issues", "issue_dependencies", "label", "milestone", "sla", "ping" };
}

public sealed class WebhookRequest
{
    [Required, MaxLength(2000)] public string TargetUrl { get; set; } = string.Empty;
    /// <summary>Rỗng = mọi event.</summary>
    public List<string>? Events { get; set; }
    public bool? IsActive { get; set; }
    /// <summary>Để trống → hệ thống sinh secret (chỉ trả lại một lần khi tạo).</summary>
    [MaxLength(200)] public string? Secret { get; set; }
}

public sealed record WebhookResponse(Guid Id, string? Project, string TargetUrl, IReadOnlyList<string> Events, bool IsActive,
    int ConsecutiveFailures, DateTimeOffset CreatedAt, string? Secret);

public sealed record WebhookDeliveryResponse(Guid Id, string Event, string Action, int? HttpStatus, int? DurationMs, string? Error,
    bool IsRedelivery, int Attempt, DateTimeOffset? NextAttemptAt, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt, JsonElement RequestHeaders, string RequestBody, string? ResponseBody);

/// <summary>Message nội bộ cho event không thuộc ticket (label/milestone CRUD) — Dispatcher tiêu thụ.</summary>
public sealed record WebhookEventRaised(string Event, string Action, Guid? ProjectId, string PayloadJson, Guid? SenderId);

/// <summary>UC-15 / mục 6.7 (Architecture v3.1).</summary>
public sealed class WebhookService
{
    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly IPublishEndpoint _publish;
    private readonly TicketingOptions _options;
    private readonly TimeProvider _clock;

    public WebhookService(AppDbContext db, TicketQueries q, IPublishEndpoint publish, IOptions<TicketingOptions> options, TimeProvider clock)
    {
        _db = db;
        _q = q;
        _publish = publish;
        _options = options.Value;
        _clock = clock;
    }

    public async Task<IReadOnlyList<WebhookResponse>> ListAsync(string slug, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var rows = await _db.WebhookSubscriptions.AsNoTracking().Where(w => w.ProjectId == project.Id).OrderBy(w => w.CreatedAt).ToListAsync(ct);
        return rows.Select(w => ToResponse(w, project.Slug, null)).ToList();
    }

    public async Task<WebhookResponse> CreateAsync(string slug, WebhookRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        ValidateUrl(request.TargetUrl);
        var events = NormalizeEvents(request.Events);
        var secret = string.IsNullOrWhiteSpace(request.Secret) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() : request.Secret.Trim();
        var hook = new WebhookSubscription
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, TargetUrl = request.TargetUrl.Trim(), SecretHmac = secret,
            Events = JsonSerializer.Serialize(events), IsActive = request.IsActive ?? true, CreatedById = user.GetUserId(), CreatedAt = _clock.GetUtcNow()
        };
        _db.WebhookSubscriptions.Add(hook);
        await _db.SaveChangesAsync(ct);
        return ToResponse(hook, project.Slug, secret);
    }

    public async Task<WebhookResponse> UpdateAsync(string slug, Guid id, WebhookRequest request, CancellationToken ct)
    {
        var (project, hook) = await FindAsync(slug, id, ct);
        ValidateUrl(request.TargetUrl);
        hook.TargetUrl = request.TargetUrl.Trim();
        hook.Events = JsonSerializer.Serialize(NormalizeEvents(request.Events));
        if (request.IsActive is { } active)
        {
            hook.IsActive = active;
            if (active) hook.ConsecutiveFailures = 0;
        }
        if (!string.IsNullOrWhiteSpace(request.Secret)) hook.SecretHmac = request.Secret.Trim();
        await _db.SaveChangesAsync(ct);
        return ToResponse(hook, project.Slug, null);
    }

    public async Task DeleteAsync(string slug, Guid id, CancellationToken ct)
    {
        var (_, hook) = await FindAsync(slug, id, ct);
        _db.WebhookSubscriptions.Remove(hook);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WebhookDeliveryResponse>> DeliveriesAsync(string slug, Guid id, int take, CancellationToken ct)
    {
        var (_, hook) = await FindAsync(slug, id, ct);
        var rows = await _db.WebhookDeliveries.AsNoTracking().Where(d => d.SubscriptionId == hook.Id)
            .OrderByDescending(d => d.CreatedAt).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        return rows.Select(ToResponse).ToList();
    }

    /// <summary>Redeliver: gửi lại đúng payload đã lưu (mục 6.7) — tạo delivery mới với <c>is_redelivery</c>.</summary>
    public async Task<WebhookDeliveryResponse> RedeliverAsync(string slug, Guid id, Guid deliveryId, CancellationToken ct)
    {
        var (_, hook) = await FindAsync(slug, id, ct);
        var original = await _db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deliveryId && d.SubscriptionId == hook.Id, ct)
                       ?? throw AppException.NotFound("Không tìm thấy delivery.");
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = hook.Id, EventId = original.EventId, EventName = original.EventName, Action = original.Action,
            RequestBody = original.RequestBody, IsRedelivery = true, Attempt = 1, NextAttemptAt = _clock.GetUtcNow(), CreatedAt = _clock.GetUtcNow()
        };
        _db.WebhookDeliveries.Add(delivery);
        // Bus outbox: Publish phải đứng TRƯỚC SaveChanges để message được ghi cùng transaction.
        await _publish.Publish(new WebhookDeliveryRequested(delivery.Id), ct);
        await _db.SaveChangesAsync(ct);
        return ToResponse(delivery);
    }

    public async Task<WebhookDeliveryResponse> PingAsync(string slug, Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        var (project, hook) = await FindAsync(slug, id, ct);
        var body = JsonSerializer.Serialize(new { zen = "Keep it logically awesome.", hook_id = hook.Id, project = new { project.Slug, project.Name } }, TicketEventStore.PayloadJson);
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(), SubscriptionId = hook.Id, EventName = "ping", Action = "ping", RequestBody = body,
            Attempt = 1, NextAttemptAt = _clock.GetUtcNow(), CreatedAt = _clock.GetUtcNow()
        };
        _db.WebhookDeliveries.Add(delivery);
        await _publish.Publish(new WebhookDeliveryRequested(delivery.Id), ct);
        await _db.SaveChangesAsync(ct);
        return ToResponse(delivery);
    }

    /// <summary>BR-SEC-05 / 6.7: chỉ https (http chỉ khi cho phép mạng nội bộ ở dev) và chặn SSRF.</summary>
    public void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) throw AppException.BadRequest("target_url không hợp lệ.");
        var allowPrivate = _options.Webhooks.AllowPrivateNetworks;
        if (uri.Scheme != "https" && !(allowPrivate && uri.Scheme == "http")) throw AppException.BadRequest("target_url phải dùng https.");
        if (!allowPrivate && IsPrivateHost(uri.Host)) throw AppException.BadRequest("target_url trỏ vào mạng nội bộ/loopback (chặn SSRF).");
    }

    public static bool IsPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip))
        {
            try { ip = Dns.GetHostAddresses(host).FirstOrDefault(); } catch { return true; }
            if (ip is null) return true;
        }
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] == 0;
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6Any);
    }

    private static List<string> NormalizeEvents(List<string>? events)
    {
        var list = (events ?? new()).Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
        var unknown = list.Except(WebhookEvents.All).ToList();
        if (unknown.Count > 0) throw AppException.BadRequest($"Event không hợp lệ: {string.Join(", ", unknown)}. Chấp nhận: {string.Join(", ", WebhookEvents.All)}.");
        return list;
    }

    private async Task<(Project, WebhookSubscription)> FindAsync(string slug, Guid id, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var hook = await _db.WebhookSubscriptions.FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == project.Id, ct)
                   ?? throw AppException.NotFound("Không tìm thấy webhook.");
        return (project, hook);
    }

    private static WebhookResponse ToResponse(WebhookSubscription w, string? slug, string? secret)
        => new(w.Id, slug, w.TargetUrl, JsonSerializer.Deserialize<List<string>>(w.Events) ?? new(), w.IsActive, w.ConsecutiveFailures, w.CreatedAt, secret);

    public static WebhookDeliveryResponse ToResponse(WebhookDelivery d)
        => new(d.Id, d.EventName, d.Action, d.HttpStatus, d.DurationMs, d.Error, d.IsRedelivery, d.Attempt, d.NextAttemptAt, d.CreatedAt, d.DeliveredAt,
            JsonDocument.Parse(string.IsNullOrWhiteSpace(d.RequestHeaders) ? "{}" : d.RequestHeaders).RootElement.Clone(), d.RequestBody, d.ResponseBody);
}

/// <summary>Yêu cầu gửi (hoặc gửi lại) một delivery đã lưu.</summary>
public sealed record WebhookDeliveryRequested(Guid DeliveryId);

[ApiController]
[Route("api/projects/{project}/webhooks")]
[Produces("application/json")]
public sealed class WebhooksController : ControllerBase
{
    private readonly WebhookService _webhooks;
    public WebhooksController(WebhookService webhooks) => _webhooks = webhooks;

    [HttpGet]
    [RequirePermission(Permissions.WebhookManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<WebhookResponse>>> List(string project, CancellationToken ct)
        => Ok(await _webhooks.ListAsync(project, ct));

    [HttpPost]
    [RequirePermission(Permissions.WebhookManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<WebhookResponse>> Create(string project, WebhookRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _webhooks.CreateAsync(project, request, User, ct));

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.WebhookManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<WebhookResponse>> Update(string project, Guid id, WebhookRequest request, CancellationToken ct)
        => Ok(await _webhooks.UpdateAsync(project, id, request, ct));

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.WebhookManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> Delete(string project, Guid id, CancellationToken ct)
    {
        await _webhooks.DeleteAsync(project, id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/deliveries")]
    [RequirePermission(Permissions.WebhookManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<WebhookDeliveryResponse>>> Deliveries(string project, Guid id, [FromQuery] int take = 30, CancellationToken ct = default)
        => Ok(await _webhooks.DeliveriesAsync(project, id, take, ct));

    [HttpPost("{id:guid}/deliveries/{deliveryId:guid}/redeliver")]
    [RequirePermission(Permissions.WebhookManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<WebhookDeliveryResponse>> Redeliver(string project, Guid id, Guid deliveryId, CancellationToken ct)
        => StatusCode(StatusCodes.Status202Accepted, await _webhooks.RedeliverAsync(project, id, deliveryId, ct));

    [HttpPost("{id:guid}/pings")]
    [RequirePermission(Permissions.WebhookManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<WebhookDeliveryResponse>> Ping(string project, Guid id, CancellationToken ct)
        => StatusCode(StatusCodes.Status202Accepted, await _webhooks.PingAsync(project, id, User, ct));
}
