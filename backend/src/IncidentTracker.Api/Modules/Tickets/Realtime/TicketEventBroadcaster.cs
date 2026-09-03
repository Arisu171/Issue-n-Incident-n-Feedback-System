using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Realtime;

/// <summary>
/// Consumer MassTransit (mục 6.2 bước "SignalR Broadcast Signal"): nạp event từ Event Store, render
/// thân Markdown (BR-SEC-02: sanitize trước broadcast), rồi đẩy xuống đúng group theo visibility
/// (BR-EV-03, BR-SEC-06). INTERNAL không bao giờ đi tới group công khai.
/// </summary>
public sealed class TicketEventBroadcaster : IConsumer<TicketEventAppended>
{
    private readonly AppDbContext _db;
    private readonly IHubContext<TicketHub> _hub;
    private readonly MarkdownRenderer _renderer;
    private readonly ILogger<TicketEventBroadcaster> _logger;

    public TicketEventBroadcaster(AppDbContext db, IHubContext<TicketHub> hub, MarkdownRenderer renderer,
        ILogger<TicketEventBroadcaster> logger)
    {
        _db = db;
        _hub = hub;
        _renderer = renderer;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<TicketEventAppended> context)
    {
        var msg = context.Message;
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = msg.CorrelationId ?? string.Empty,
            ["TicketId"] = msg.TicketId,
            ["EventId"] = msg.EventId
        });

        var evt = await _db.TicketEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == msg.EventId, context.CancellationToken);
        if (evt is null)
        {
            _logger.LogWarning("Broadcaster: không tìm thấy event {EventId}.", msg.EventId);
            return;
        }

        var slug = await _db.Projects.Where(p => p.Id == msg.ProjectId).Select(p => p.Slug)
            .FirstOrDefaultAsync(context.CancellationToken) ?? "support";

        UserSummary? actor = null;
        if (evt.ActorId is { } actorId)
        {
            var u = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == actorId, context.CancellationToken);
            actor = u is null ? null : UserSummary.From(u);
        }

        // Danh sách login có thật phải truyền vào, y như đường REST: thiếu nó thì `ToDto` nhận
        // `null` và bộ render coi **mọi** `@chữ` là người có thật. Hệ quả: bình luận tới trực
        // tiếp thì `@khong-ton-tai` thành link chết, còn sau khi tải lại thì lại là chữ thường —
        // hai đường cùng render một nội dung mà ra hai kết quả.
        var body = TimelineMapper.BodyOf(evt);
        var knownLogins = await KnownLoginsAsync(body, context.CancellationToken);

        var dto = TimelineMapper.ToDto(evt, actor, _renderer, slug, knownLogins);

        // Event im lặng không đẩy thành dòng: truy vấn timeline không trả chúng, nên đẩy đi là
        // dựng ra một dòng mà lần tải kế tiếp sẽ xoá — người dùng thấy nó hiện lên rồi biến mất.
        // Tín hiệu `TicketChanged` bên dưới vẫn gửi, vì đó mới là thứ báo "có gì đó vừa đổi".
        if (TicketEventTypes.IsSilent(evt.EventType))
        {
            await NotifyChangedAsync(evt, msg, context.CancellationToken);
            return;
        }

        switch (evt.Visibility)
        {
            case EventVisibility.Public:
                await _hub.Clients.Group(TicketHub.TicketGroup(evt.TicketId)).SendAsync(TicketHub.ReceiveEvent, dto, context.CancellationToken);
                break;
            case EventVisibility.Internal:
                await _hub.Clients.Group(TicketHub.InternalGroup(evt.TicketId)).SendAsync(TicketHub.ReceiveEvent, dto, context.CancellationToken);
                break;
            case EventVisibility.ActorOnly when evt.ActorId is { } subject:
                await _hub.Clients.Group(TicketHub.UserGroup(subject)).SendAsync(TicketHub.ReceiveEvent, dto, context.CancellationToken);
                break;
        }

        await NotifyChangedAsync(evt, msg, context.CancellationToken);
    }

    /// <summary>Login có thật trong số những cái được nhắc tới — cùng phép tra mà đường REST dùng.</summary>
    private async Task<IReadOnlySet<string>> KnownLoginsAsync(string? markdown, CancellationToken ct)
    {
        var candidates = MarkdownRenderer.ExtractMentions(markdown);
        if (candidates.Count == 0) return new HashSet<string>(StringComparer.Ordinal);

        var found = await _db.Users.AsNoTracking()
            .Where(u => candidates.Contains(u.Login)).Select(u => u.Login).ToListAsync(ct);
        return found.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Tín hiệu nhẹ để mọi người đang xem reload snapshot (labels, state, version...).</summary>
    private Task NotifyChangedAsync(Domain.TicketEvent evt, TicketEventAppended msg, CancellationToken ct)
        => _hub.Clients.Group(TicketHub.TicketGroup(evt.TicketId))
            .SendAsync(TicketHub.TicketChanged,
                new { ticketId = evt.TicketId, version = evt.TicketVersion, eventType = evt.EventType }, ct);
}
