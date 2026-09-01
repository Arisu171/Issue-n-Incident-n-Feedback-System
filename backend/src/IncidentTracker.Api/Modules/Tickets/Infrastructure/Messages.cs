namespace IncidentTracker.Api.Modules.Tickets.Infrastructure;

/// <summary>
/// Message phát qua MassTransit (bus outbox) mỗi khi một event được append vào Event Store
/// (Architecture v3.1, mục 6.2). Consumer nạp lại event từ DB theo <see cref="Sequence"/> —
/// message chỉ mang định danh, không mang payload, để Event Store luôn là nguồn sự thật.
/// </summary>
public sealed record TicketEventAppended(
    long Sequence,
    Guid EventId,
    Guid TicketId,
    Guid ProjectId,
    int TicketNumber,
    int TicketVersion,
    string EventType,
    Guid? ActorId,
    string Visibility,
    DateTimeOffset CreatedAt,
    string? CorrelationId);
