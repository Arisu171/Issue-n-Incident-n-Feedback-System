using System.Text.Json;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;

namespace IncidentTracker.Api.Modules.Tickets.Infrastructure;

/// <summary>Người dùng rút gọn trong mọi response của module Tickets — không lộ email cho Customer.</summary>
public sealed record UserSummary(Guid Id, string Login, string DisplayName)
{
    public static UserSummary From(User u) => new(u.Id, u.Login, u.DisplayName);
}

/// <summary>
/// Một dòng trên timeline (Architecture v3.1 mục 5.4, 6.5). <c>Payload</c> giữ nguyên jsonb;
/// <c>BodyHtml</c> chỉ có với event có thân Markdown (đã render + sanitize, BR-SEC-02).
/// Các cờ <c>IsEdited/IsHidden/IsDeleted</c> do TimelineService tính từ event bù trừ.
/// </summary>
public sealed record TimelineEventDto(
    long Sequence,
    Guid Id,
    Guid TicketId,
    int TicketVersion,
    string EventType,
    UserSummary? Actor,
    JsonElement Payload,
    string? Body,
    string? BodyHtml,
    EventVisibility Visibility,
    DateTimeOffset CreatedAt,
    bool IsEdited = false,
    DateTimeOffset? EditedAt = null,
    bool IsHidden = false,
    HideReason? HiddenReason = null,
    IReadOnlyDictionary<string, int>? Reactions = null,
    IReadOnlyList<ReactionType>? ViewerReactions = null,
    string? AuthorAssociation = null);

/// <summary>Chuyển entity → DTO, dùng chung cho REST và SignalR để hai kênh trả cùng một hình dạng.</summary>
public static class TimelineMapper
{
    /// <summary>
    /// Thân Markdown của event, nếu loại event này có thân.
    ///
    /// Tách ra để nơi gọi tra được danh sách login có thật **trước khi** render — cùng một phép
    /// đọc payload, không phải đoán lại ở chỗ khác.
    /// </summary>
    public static string? BodyOf(TicketEvent evt)
    {
        if (!TicketEventTypes.HasBody(evt.EventType)) return null;
        var payload = TicketEventStore.ParsePayload(evt);
        return payload.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
    }

    public static TimelineEventDto ToDto(TicketEvent evt, UserSummary? actor, MarkdownRenderer renderer,
        string projectSlug, IReadOnlySet<string>? knownLogins = null)
    {
        var payload = TicketEventStore.ParsePayload(evt);
        string? body = null;
        string? html = null;

        if (TicketEventTypes.HasBody(evt.EventType) && payload.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String)
        {
            body = b.GetString();
            html = renderer.Render(body, projectSlug, knownLogins).Html;
        }

        return new TimelineEventDto(
            evt.Sequence, evt.Id, evt.TicketId, evt.TicketVersion, evt.EventType, actor,
            payload, body, html, evt.Visibility, evt.CreatedAt);
    }
}
