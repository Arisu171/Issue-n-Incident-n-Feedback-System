using System.Collections.Concurrent;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Tickets.Realtime;

/// <summary>
/// UC-05 / mục 6.6 (Architecture v3.1). Đường dẫn <c>/ticket-hub</c>, JWT truyền qua query string
/// <c>access_token</c> (cấu hình ở Program.cs). Group:
/// <list type="bullet">
/// <item><c>user_{id}</c> — thông báo cá nhân, event ACTOR_ONLY, số unread.</item>
/// <item><c>ticket_{id}</c> — event PUBLIC của ticket đang mở.</item>
/// <item><c>ticket_{id}_internal</c> — event INTERNAL; chỉ người có <c>ticket.internal_note</c> được vào.</item>
/// </list>
/// Quyết định "ai được vào group nào" nằm ở server; client chỉ xin vào.
/// </summary>
[Authorize]
public sealed class TicketHub : Hub
{
    public const string Path = "/ticket-hub";
    public const string ReceiveEvent = "ReceiveEvent";
    public const string TicketChanged = "TicketChanged";
    public const string NotificationChanged = "NotificationChanged";

    private readonly Modules.Identity.PresenceTracker _presence;

    public TicketHub(Modules.Identity.PresenceTracker presence) => _presence = presence;

    public static string UserGroup(Guid userId) => $"user_{userId}";
    public static string TicketGroup(Guid ticketId) => $"ticket_{ticketId}";
    public static string InternalGroup(Guid ticketId) => $"ticket_{ticketId}_internal";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(user.GetUserId()));
        // Hiện diện suy ra từ kết nối đang mở, không cần nhịp tim riêng: hub này vốn đã biết ai
        // vào ai ra.
        _presence.Connected(user.GetUserId());
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _presence.Disconnected(Context.User!.GetUserId());
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Client mở trang ticket → xin vào group. Nhân viên tự động vào cả group nội bộ.</summary>
    public async Task JoinTicket(Guid ticketId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, TicketGroup(ticketId));
        if (TimelineAccess.CanSeeInternal(Context.User!))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, InternalGroup(ticketId));
        }
    }

    public async Task LeaveTicket(Guid ticketId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, TicketGroup(ticketId));
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, InternalGroup(ticketId));
    }
}

/// <summary>
/// BR-SEC-03 cho WebSocket: giới hạn số lời gọi hub mỗi phút trên từng connection để chống spam
/// real-time. Bộ đếm in-memory (một instance — sai lệch có chủ đích so với Redis của bản vẽ).
/// </summary>
public sealed class HubRateLimitFilter : IHubFilter
{
    private readonly ConcurrentDictionary<string, (long WindowStart, int Count)> _counters = new();
    private readonly IOptionsMonitor<TicketingOptions> _options;
    private readonly TimeProvider _clock;

    public HubRateLimitFilter(IOptionsMonitor<TicketingOptions> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var limit = _options.CurrentValue.HubInvocationsPerMinute;
        var nowMinute = _clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        var id = invocationContext.Context.ConnectionId;

        var (start, count) = _counters.AddOrUpdate(id,
            _ => (nowMinute, 1),
            (_, cur) => cur.WindowStart == nowMinute ? (cur.WindowStart, cur.Count + 1) : (nowMinute, 1));

        if (count > limit)
        {
            throw new HubException($"Quá {limit} lời gọi/phút trên một kết nối. Thử lại sau.");
        }

        return await next(invocationContext);
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        _counters.TryRemove(context.Context.ConnectionId, out _);
        return next(context, exception);
    }
}
