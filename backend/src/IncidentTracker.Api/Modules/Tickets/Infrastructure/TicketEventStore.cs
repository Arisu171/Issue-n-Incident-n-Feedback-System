using System.Text.Json;
using System.Text.Json.Serialization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Infrastructure;

/// <summary>
/// Cửa duy nhất để ghi vào Event Store (Architecture v3.1, mục 5.3, BR-EV-01/02, BR-CONC-01).
///
/// <para>Mỗi lần append: <c>ticket_version = ticket.Version + 1</c>, cập nhật snapshot
/// <c>Version/UpdatedAt</c>, và publish <see cref="TicketEventAppended"/> qua MassTransit bus outbox
/// — message chỉ được giao sau khi <c>SaveChanges</c> commit, nên không bao giờ có "message mà
/// không có event". Hai request ghi song song cùng ticket sẽ va vào unique
/// <c>(ticket_id, ticket_version, created_at)</c>; caller dịch lỗi đó thành 409.</para>
///
/// <para>Không gọi <c>SaveChanges</c> ở đây: caller gom nhiều event + projection đồng bộ vào một
/// transaction rồi lưu một lần.</para>
/// </summary>
public sealed class TicketEventStore
{
    public static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly AppDbContext _db;
    private readonly IPublishEndpoint _publish;
    private readonly IHttpContextAccessor _http;
    private readonly TimeProvider _clock;
    private readonly IncidentTracker.Api.Observability.AppMetrics _metrics;

    public TicketEventStore(AppDbContext db, IPublishEndpoint publish, IHttpContextAccessor http, TimeProvider clock,
        IncidentTracker.Api.Observability.AppMetrics metrics)
    {
        _db = db;
        _publish = publish;
        _http = http;
        _clock = clock;
        _metrics = metrics;
    }

    /// <summary>
    /// Append một event. <paramref name="actorId"/> = null nghĩa là hệ thống. Với
    /// <see cref="EventVisibility.ActorOnly"/>, quy ước <paramref name="actorId"/> là người
    /// <b>bị ảnh hưởng</b> (người được mention/subscribe) để bộ lọc visibility là một điều kiện SQL.
    /// </summary>
    public async Task<TicketEvent> AppendAsync(
        Ticket ticket,
        string eventType,
        object? payload,
        Guid? actorId,
        EventVisibility visibility = EventVisibility.Public,
        DateTimeOffset? at = null,
        CancellationToken ct = default)
    {
        var now = at ?? _clock.GetUtcNow();
        var correlation = _http.HttpContext is { } ctx ? CorrelationIdMiddleware.Current(ctx) : null;

        var evt = new TicketEvent
        {
            Id = Guid.NewGuid(),
            TicketId = ticket.Id,
            TicketVersion = ticket.Version + 1,
            ActorId = actorId,
            EventType = eventType,
            Payload = payload is null ? "{}" : JsonSerializer.Serialize(payload, PayloadJson),
            Visibility = visibility,
            CorrelationId = Guid.TryParse(correlation, out var cid) ? cid : null,
            CreatedAt = now
        };

        ticket.Version = evt.TicketVersion;
        ticket.UpdatedAt = now;

        _db.TicketEvents.Add(evt);
        _metrics.TicketEventAppended(eventType);

        // Mục 2.8 / bảng "projection đồng bộ" (mục 5): auto-subscribe ngay trong transaction của event
        // để worker thông báo (bất đồng bộ) luôn thấy subscription — tránh race khi comment ngay sau tạo.
        await AutoSubscribeAsync(ticket.Id, eventType, actorId, payload, now, ct);

        // Sequence chỉ có sau khi INSERT; consumer nạp lại theo EventId nên message vẫn đủ dùng.
        // Bus outbox lưu message vào bảng outbox trong cùng SaveChanges với event.
        await _publish.Publish(new TicketEventAppended(
            0, evt.Id, ticket.Id, ticket.ProjectId, ticket.Number, evt.TicketVersion, eventType,
            actorId, EnumNaming.Format(visibility), now, correlation), ct);

        return evt;
    }

    private async Task AutoSubscribeAsync(Guid ticketId, string eventType, Guid? actorId, object? payload, DateTimeOffset now, CancellationToken ct)
    {
        (Guid UserId, SubscriptionReason Reason)? target = eventType switch
        {
            TicketEventTypes.Opened when actorId is { } a => (a, SubscriptionReason.Author),
            TicketEventTypes.Commented or TicketEventTypes.InternalNote when actorId is { } c => (c, SubscriptionReason.Commented),
            TicketEventTypes.Closed or TicketEventTypes.Reopened when actorId is { } s => (s, SubscriptionReason.StateChange),
            TicketEventTypes.Mentioned when actorId is { } m => (m, SubscriptionReason.Mentioned),
            TicketEventTypes.Assigned => AssigneeOf(payload) is { } assignee ? (assignee, SubscriptionReason.Assigned) : null,
            _ => null
        };
        if (target is null) return;
        var (userId, reason) = target.Value;

        var sub = _db.TicketSubscriptions.Local.FirstOrDefault(x => x.TicketId == ticketId && x.UserId == userId)
                  ?? await _db.TicketSubscriptions.FirstOrDefaultAsync(x => x.TicketId == ticketId && x.UserId == userId, ct);
        if (sub is null)
        {
            _db.TicketSubscriptions.Add(new TicketSubscription { TicketId = ticketId, UserId = userId, State = SubscriptionState.Subscribed, Reason = reason, UpdatedAt = now });
        }
        else if (sub.State == SubscriptionState.Unsubscribed && reason == SubscriptionReason.Mentioned)
        {
            // Thao tác thủ công thắng auto-subscribe, trừ khi được @mention trực tiếp (IGNORED vẫn giữ).
            sub.State = SubscriptionState.Subscribed;
            sub.Reason = reason;
            sub.UpdatedAt = now;
        }
    }

    private static Guid? AssigneeOf(object? payload)
    {
        if (payload is null) return null;
        var prop = payload.GetType().GetProperty("assignee_id");
        return prop?.GetValue(payload) is Guid g ? g : null;
    }

    /// <summary>Đọc payload jsonb thành <see cref="JsonElement"/>.</summary>
    public static JsonElement ParsePayload(TicketEvent evt)
        => JsonDocument.Parse(string.IsNullOrWhiteSpace(evt.Payload) ? "{}" : evt.Payload).RootElement.Clone();
}
