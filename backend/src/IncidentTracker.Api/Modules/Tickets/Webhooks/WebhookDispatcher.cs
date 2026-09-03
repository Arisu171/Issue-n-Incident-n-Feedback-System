using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Observability;
using IncidentTracker.Api.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Tickets.Webhooks;

/// <summary>Kết quả một lần gửi HTTP.</summary>
public sealed record WebhookHttpResult(int? StatusCode, string? ResponseBody, string? Error);

/// <summary>Tách HTTP ra interface để test thay bằng receiver giả (không mở cổng thật).</summary>
public interface IWebhookHttpClient
{
    Task<WebhookHttpResult> PostAsync(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct);
}

public sealed class HttpWebhookClient : IWebhookHttpClient
{
    private readonly IHttpClientFactory _factory;
    public HttpWebhookClient(IHttpClientFactory factory) => _factory = factory;

    public async Task<WebhookHttpResult> PostAsync(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct)
    {
        var client = _factory.CreateClient("webhooks");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers)
        {
            if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(k, v);
        }
        try
        {
            using var response = await client.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            return new WebhookHttpResult((int)response.StatusCode, text.Length > 65536 ? text[..65536] : text, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new WebhookHttpResult(null, null, ex.Message);
        }
    }
}

/// <summary>
/// Webhook Dispatcher (mục 6.1, 6.7, BR-SEC-05): map event → (event, action) theo tên của GitHub, dựng payload,
/// ký HMAC-SHA256, gửi với timeout 10 s, ghi delivery log; thất bại → lịch retry (1m, 5m, 30m, 2h, 6h) rồi DLQ.
/// INTERNAL_NOTE và mọi event INTERNAL không bao giờ được phát.
/// </summary>
public sealed class WebhookDispatcher : IConsumer<TicketEventAppended>, IConsumer<WebhookEventRaised>, IConsumer<WebhookDeliveryRequested>
{
    public static readonly TimeSpan[] Backoff = { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6) };
    public const int MaxAttempts = 6; // lần đầu + 5 retry
    public const int DisableAfterConsecutiveFailures = 100;

    private static readonly Dictionary<string, (string Event, string Action)> Map = new(StringComparer.Ordinal)
    {
        [TicketEventTypes.Opened] = ("issues", "opened"),
        [TicketEventTypes.Renamed] = ("issues", "edited"),
        [TicketEventTypes.BodyEdited] = ("issues", "edited"),
        [TicketEventTypes.Deleted] = ("issues", "deleted"),
        [TicketEventTypes.Transferred] = ("issues", "transferred"),
        [TicketEventTypes.Pinned] = ("issues", "pinned"),
        [TicketEventTypes.Unpinned] = ("issues", "unpinned"),
        [TicketEventTypes.Closed] = ("issues", "closed"),
        [TicketEventTypes.Reopened] = ("issues", "reopened"),
        [TicketEventTypes.Assigned] = ("issues", "assigned"),
        [TicketEventTypes.Unassigned] = ("issues", "unassigned"),
        [TicketEventTypes.Labeled] = ("issues", "labeled"),
        [TicketEventTypes.Unlabeled] = ("issues", "unlabeled"),
        [TicketEventTypes.Locked] = ("issues", "locked"),
        [TicketEventTypes.Unlocked] = ("issues", "unlocked"),
        [TicketEventTypes.Milestoned] = ("issues", "milestoned"),
        [TicketEventTypes.Demilestoned] = ("issues", "demilestoned"),
        [TicketEventTypes.Typed] = ("issues", "typed"),
        [TicketEventTypes.Untyped] = ("issues", "untyped"),
        [TicketEventTypes.Commented] = ("issue_comment", "created"),
        [TicketEventTypes.CommentEdited] = ("issue_comment", "edited"),
        [TicketEventTypes.CommentDeleted] = ("issue_comment", "deleted"),
        [TicketEventTypes.SubIssueAdded] = ("sub_issues", "sub_issue_added"),
        [TicketEventTypes.SubIssueRemoved] = ("sub_issues", "sub_issue_removed"),
        [TicketEventTypes.ParentIssueAdded] = ("sub_issues", "parent_issue_added"),
        [TicketEventTypes.ParentIssueRemoved] = ("sub_issues", "parent_issue_removed"),
        [TicketEventTypes.BlockedByAdded] = ("issue_dependencies", "blocked_by_added"),
        [TicketEventTypes.BlockedByRemoved] = ("issue_dependencies", "blocked_by_removed"),
        [TicketEventTypes.BlockingAdded] = ("issue_dependencies", "blocking_added"),
        [TicketEventTypes.BlockingRemoved] = ("issue_dependencies", "blocking_removed"),
        [TicketEventTypes.SlaWarning] = ("sla", "warning"),
        [TicketEventTypes.SlaBreached] = ("sla", "breached"),
        [TicketEventTypes.Escalated] = ("sla", "escalated")
    };

    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly IWebhookHttpClient _http;
    private readonly TimelineService _timeline;
    private readonly AppMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly TicketingOptions _options;
    private readonly ILogger<WebhookDispatcher> _logger;

    public WebhookDispatcher(AppDbContext db, TicketQueries q, IWebhookHttpClient http, TimelineService timeline, AppMetrics metrics,
        TimeProvider clock, IOptions<TicketingOptions> options, ILogger<WebhookDispatcher> logger)
    {
        _db = db;
        _q = q;
        _http = http;
        _timeline = timeline;
        _metrics = metrics;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
    }

    // ---------------- event ticket → deliveries ----------------

    public async Task Consume(ConsumeContext<TicketEventAppended> context)
    {
        var msg = context.Message;
        if (!Map.TryGetValue(msg.EventType, out var mapped)) return;
        if (msg.Visibility != "PUBLIC") return; // INTERNAL / ACTOR_ONLY không bao giờ phát ra ngoài
        var ct = context.CancellationToken;

        var hooks = await ActiveHooksAsync(msg.ProjectId, mapped.Event, ct);
        if (hooks.Count == 0) return;

        var evt = await _db.TicketEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == msg.EventId, ct);
        if (evt is null || evt.Visibility != EventVisibility.Public) return;
        var ticket = await _q.WithDetails().IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == msg.TicketId, ct);
        if (ticket is null) return;

        var body = await BuildPayloadAsync(mapped.Event, mapped.Action, evt, ticket, ct);
        foreach (var hook in hooks)
        {
            var delivery = new WebhookDelivery
            {
                Id = Guid.NewGuid(), SubscriptionId = hook.Id, EventId = evt.Id, EventName = mapped.Event, Action = mapped.Action,
                RequestBody = body, Attempt = 1, CreatedAt = _clock.GetUtcNow()
            };
            _db.WebhookDeliveries.Add(delivery);
            await _db.SaveChangesAsync(ct);
            await SendAsync(delivery, hook, ct);
        }
    }

    public async Task Consume(ConsumeContext<WebhookEventRaised> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;
        var hooks = await ActiveHooksAsync(msg.ProjectId, msg.Event, ct);
        if (hooks.Count == 0) return;
        var sender = msg.SenderId is { } sid ? await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == sid, ct) : null;
        var project = msg.ProjectId is { } pid ? await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct) : null;
        using var payload = JsonDocument.Parse(msg.PayloadJson);
        var body = JsonSerializer.Serialize(new
        {
            action = msg.Action,
            payload = payload.RootElement,
            sender = sender is null ? null : new { sender.Id, sender.Login, display_name = sender.DisplayName },
            project = project is null ? null : new { project.Id, project.Slug, project.Name }
        }, TicketEventStore.PayloadJson);

        foreach (var hook in hooks)
        {
            var delivery = new WebhookDelivery { Id = Guid.NewGuid(), SubscriptionId = hook.Id, EventName = msg.Event, Action = msg.Action, RequestBody = body, Attempt = 1, CreatedAt = _clock.GetUtcNow() };
            _db.WebhookDeliveries.Add(delivery);
            await _db.SaveChangesAsync(ct);
            await SendAsync(delivery, hook, ct);
        }
    }

    public async Task Consume(ConsumeContext<WebhookDeliveryRequested> context)
    {
        var ct = context.CancellationToken;
        var delivery = await _db.WebhookDeliveries.Include(d => d.Subscription).FirstOrDefaultAsync(d => d.Id == context.Message.DeliveryId, ct);
        if (delivery is null || delivery.DeliveredAt is not null) return;
        await SendAsync(delivery, delivery.Subscription, ct);
    }

    // ---------------- gửi ----------------

    /// <summary>Gửi một delivery; cập nhật log; lên lịch retry khi thất bại. Dùng chung với <see cref="WebhookRetryJob"/>.</summary>
    public async Task SendAsync(WebhookDelivery delivery, WebhookSubscription hook, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["User-Agent"] = "UnifiedTicketing-Hookshot/1.0",
            ["X-Event"] = delivery.EventName,
            ["X-Delivery-Id"] = delivery.Id.ToString(),
            ["X-Hook-Id"] = hook.Id.ToString(),
            ["X-Hub-Signature-256"] = "sha256=" + Sign(hook.SecretHmac, delivery.RequestBody)
        };
        delivery.RequestHeaders = JsonSerializer.Serialize(headers.Where(h => h.Key != "X-Hub-Signature-256").ToDictionary(h => h.Key, h => h.Value));

        var sw = Stopwatch.StartNew();
        var result = await _http.PostAsync(hook.TargetUrl, headers, delivery.RequestBody, ct);
        sw.Stop();

        delivery.HttpStatus = result.StatusCode;
        delivery.DurationMs = (int)sw.ElapsedMilliseconds;
        delivery.ResponseBody = result.ResponseBody;
        delivery.Error = result.Error;
        var ok = result.StatusCode is >= 200 and < 300;
        _metrics.WebhookDelivered(ok);

        // Chỉ ghi lại hàng webhook_subscriptions khi bộ đếm thật sự đổi: ba consumer xử lý ba event
        // của cùng hook song song mà cùng UPDATE một hàng sẽ va chạm (40001) và làm rollback cả delivery.
        var hookChanged = false;
        if (ok)
        {
            delivery.DeliveredAt = _clock.GetUtcNow();
            delivery.NextAttemptAt = null;
            if (hook.ConsecutiveFailures != 0) { hook.ConsecutiveFailures = 0; hookChanged = true; }
        }
        else
        {
            hook.ConsecutiveFailures += 1;
            hookChanged = true;
            if (delivery.Attempt < MaxAttempts)
            {
                delivery.NextAttemptAt = _clock.GetUtcNow() + Backoff[Math.Min(delivery.Attempt - 1, Backoff.Length - 1)];
            }
            else
            {
                delivery.NextAttemptAt = null;
                delivery.Error = "DLQ: " + (delivery.Error ?? $"HTTP {result.StatusCode}");
                _logger.LogWarning("Webhook {HookId} delivery {DeliveryId} vào DLQ sau {Attempts} lần.", hook.Id, delivery.Id, delivery.Attempt);
            }
            if (hook.ConsecutiveFailures >= DisableAfterConsecutiveFailures && hook.IsActive)
            {
                hook.IsActive = false;
                _logger.LogWarning("Webhook {HookId} bị tắt sau {Count} lần thất bại liên tiếp (mục 6.7).", hook.Id, hook.ConsecutiveFailures);
            }
        }
        if (hookChanged) _db.Entry(hook).State = EntityState.Modified;
        await _db.SaveChangesAsync(ct);
    }

    public static string Sign(string secret, string body)
        => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    private async Task<List<WebhookSubscription>> ActiveHooksAsync(Guid? projectId, string eventName, CancellationToken ct)
    {
        var hooks = await _db.WebhookSubscriptions.Where(w => w.IsActive && (w.ProjectId == null || w.ProjectId == projectId)).ToListAsync(ct);
        return hooks.Where(w =>
        {
            var events = JsonSerializer.Deserialize<List<string>>(w.Events) ?? new();
            return events.Count == 0 || events.Contains(eventName);
        }).ToList();
    }

    /// <summary>Payload theo mục 6.7: <c>{action, ticket, comment?, changes?, sender, project}</c>.</summary>
    private async Task<string> BuildPayloadAsync(string eventName, string action, TicketEvent evt, Ticket ticket, CancellationToken ct)
    {
        var payload = TicketEventStore.ParsePayload(evt);
        var sender = evt.ActorId is { } a ? await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == a, ct) : null;

        object? changes = evt.EventType switch
        {
            TicketEventTypes.Renamed => new { title = new { from = payload.TryGetProperty("from", out var f) ? f.GetString() : null } },
            TicketEventTypes.BodyEdited => new { body = new { from_hash = payload.TryGetProperty("previous_body_hash", out var h) ? h.GetString() : null } },
            _ => null
        };

        object? comment = null;
        if (eventName == "issue_comment")
        {
            var targetId = evt.EventType == TicketEventTypes.Commented ? evt.Id
                : payload.TryGetProperty("target_event_id", out var t) && t.TryGetGuid(out var tid) ? tid : evt.Id;
            var original = await _db.TicketEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == targetId, ct);
            var body = original is null ? null : await _timeline.CurrentBodyAsync(original, ct);
            comment = new { id = targetId, body, created_at = original?.CreatedAt, user = sender is null ? null : new { sender.Id, sender.Login } };
        }

        var ticketObj = new
        {
            id = ticket.Id,
            number = ticket.Number,
            title = ticket.Title,
            body = ticket.Body,
            state = EnumNaming.Format(ticket.State),
            state_reason = ticket.StateReason is { } sr ? EnumNaming.Format(sr) : null,
            locked = ticket.IsLocked,
            active_lock_reason = ticket.ActiveLockReason is { } lr ? EnumNaming.Format(lr) : null,
            pinned = ticket.IsPinned,
            user = new { ticket.Author.Id, ticket.Author.Login, display_name = ticket.Author.DisplayName },
            labels = ticket.Labels.Select(l => new { l.Label.Id, l.Label.Name, color = l.Label.ColorHex }).ToList(),
            assignees = ticket.Assignees.Select(x => new { x.User.Id, x.User.Login }).ToList(),
            milestone = ticket.Milestone is null ? null : new { ticket.Milestone.Id, ticket.Milestone.Number, ticket.Milestone.Title },
            type = ticket.Type is null ? null : new { ticket.Type.Id, ticket.Type.Name },
            priority = ticket.Priority is { } p ? EnumNaming.Format(p) : null,
            comments = ticket.CommentsCount,
            created_at = ticket.CreatedAt,
            updated_at = ticket.UpdatedAt,
            closed_at = ticket.ClosedAt,
            html_url = $"{_options.PublicBaseUrl.TrimEnd('/')}/projects/{ticket.Project.Slug}/issues/{ticket.Number}"
        };

        return JsonSerializer.Serialize(new
        {
            action,
            ticket = ticketObj,
            comment,
            changes,
            @event = new { id = evt.Id, type = evt.EventType, payload = payload, created_at = evt.CreatedAt },
            sender = sender is null ? null : new { sender.Id, sender.Login, display_name = sender.DisplayName },
            project = new { ticket.Project.Id, ticket.Project.Slug, ticket.Project.Name }
        }, TicketEventStore.PayloadJson);
    }
}

/// <summary>Gửi lại delivery đến hạn <c>next_attempt_at</c> (retry backoff) — sống sót restart vì lịch nằm trong DB.</summary>
public sealed class WebhookRetryJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<TicketingOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<WebhookRetryJob> _logger;

    public WebhookRetryJob(IServiceScopeFactory scopes, IOptionsMonitor<TicketingOptions> options, TimeProvider clock, ILogger<WebhookRetryJob> logger)
    {
        _scopes = scopes;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = _options.CurrentValue.Webhooks.RetryScanSeconds;
        if (seconds <= 0) return;
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "WebhookRetryJob thất bại; thử lại ở chu kỳ sau."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<WebhookDispatcher>();
        var now = _clock.GetUtcNow();
        var due = await db.WebhookDeliveries.Include(d => d.Subscription)
            .Where(d => d.NextAttemptAt != null && d.NextAttemptAt <= now && d.DeliveredAt == null && d.Subscription.IsActive)
            .OrderBy(d => d.NextAttemptAt).Take(50).ToListAsync(ct);
        foreach (var d in due)
        {
            d.Attempt += 1;
            await dispatcher.SendAsync(d, d.Subscription, ct);
        }
        // Dọn log > 30 ngày (mục 6.7).
        await db.WebhookDeliveries.Where(d => d.CreatedAt < now.AddDays(-30)).ExecuteDeleteAsync(ct);
    }
}
