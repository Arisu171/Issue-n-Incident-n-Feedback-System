using System.Text.Json;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Relations;

/// <summary>
/// "Regex Worker" của mục 6.2 (Architecture v3.1): tiêu thụ event có thân Markdown, bóc <c>@login</c>
/// và <c>#N</c>/<c>project#N</c>, ghi <c>MENTIONED</c> (ACTOR_ONLY, actor = người được nhắc — quy ước
/// modify_02) và <c>CROSS_REFERENCED</c> trên ticket được nhắc (BR-SOCIAL-04, 5.4). Idempotent nhờ inbox
/// MassTransit + kiểm tra event đã tồn tại theo <c>source_event_id</c>.
///
/// <para>Closing keyword trong comment/body KHÔNG đóng ticket (BR-REL-06: chỉ PR/commit) — chỉ tạo
/// cross-reference.</para>
/// </summary>
public sealed class ReferenceWorker : IConsumer<TicketEventAppended>
{
    private static readonly HashSet<string> BodyEvents = new(StringComparer.Ordinal)
    {
        TicketEventTypes.Opened, TicketEventTypes.Commented, TicketEventTypes.BodyEdited, TicketEventTypes.CommentEdited, TicketEventTypes.InternalNote
    };

    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly TicketEventStore _events;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReferenceWorker> _logger;

    public ReferenceWorker(AppDbContext db, TicketQueries q, TicketEventStore events, TimeProvider clock, ILogger<ReferenceWorker> logger)
    {
        _db = db;
        _q = q;
        _events = events;
        _clock = clock;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<TicketEventAppended> context)
    {
        var msg = context.Message;
        if (!BodyEvents.Contains(msg.EventType)) return;
        var ct = context.CancellationToken;

        var evt = await _db.TicketEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == msg.EventId, ct);
        if (evt is null) return;
        var payload = TicketEventStore.ParsePayload(evt);
        if (!payload.TryGetProperty("body", out var b) || b.ValueKind != JsonValueKind.String) return;
        var body = b.GetString() ?? string.Empty;

        var mentions = MarkdownRenderer.ExtractMentions(body);
        var references = MarkdownRenderer.ExtractReferences(body);
        if (mentions.Count == 0 && references.Count == 0) return;

        await _q.InTransactionAsync(async () =>
        {
            await _q.LockTicketRowAsync(msg.TicketId, ct);
            var source = await _q.LoadByIdAsync(msg.TicketId, true, ct);
            var now = _clock.GetUtcNow();
            var isInternal = evt.Visibility == EventVisibility.Internal;

            // ---- @mention ----
            if (mentions.Count > 0)
            {
                var users = await _db.Users.AsNoTracking().Include(u => u.UserRoles).ThenInclude(r => r.Role).ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
                    .Where(u => mentions.Contains(u.Login) && u.IsActive).ToListAsync(ct);
                foreach (var u in users)
                {
                    if (u.Id == evt.ActorId) continue; // tự nhắc mình: không thông báo
                    var codes = u.UserRoles.SelectMany(r => r.Role.RolePermissions).Select(rp => rp.Permission.Code).ToHashSet();
                    if (!codes.Contains(Permissions.TicketRead)) continue;
                    if (isInternal && !codes.Contains(Permissions.TicketInternalNote)) continue; // BR-SOCIAL-04
                    if (source.Project.CustomersSeeOnlyOwn && !codes.Contains(Permissions.TicketTriage) && source.AuthorId != u.Id && source.Assignees.All(a => a.UserId != u.Id)) continue;

                    if (await AlreadyAsync(source.Id, TicketEventTypes.Mentioned, evt.Id, u.Id, ct)) continue;
                    await _events.AppendAsync(source, TicketEventTypes.Mentioned,
                        new { user_id = u.Id, login = u.Login, by = evt.ActorId, source_event_id = evt.Id, source_event_type = evt.EventType },
                        u.Id, EventVisibility.ActorOnly, now, ct);
                }
            }

            // ---- #N cross-reference ----
            foreach (var r in references.DistinctBy(x => (x.ProjectSlug ?? source.Project.Slug, x.Number)))
            {
                var slug = r.ProjectSlug ?? source.Project.Slug;
                var target = await _q.WithDetails(tracking: true).FirstOrDefaultAsync(t => t.Project.Slug == slug && t.Number == r.Number, ct);
                if (target is null || target.Id == source.Id) continue;
                await _q.LockTicketRowAsync(target.Id, ct);
                if (await AlreadyAsync(target.Id, TicketEventTypes.CrossReferenced, evt.Id, null, ct)) continue;

                await _events.AppendAsync(target, TicketEventTypes.CrossReferenced,
                    new { source_ticket_id = source.Id, source_number = source.Number, source_project = source.Project.Slug, source_title = source.Title, source_event_id = evt.Id, source_event_type = evt.EventType },
                    evt.ActorId, isInternal ? EventVisibility.Internal : EventVisibility.Public, now, ct);

                if (!await _db.TicketReferences.AnyAsync(x => x.SourceId == source.Id && x.TargetId == target.Id && x.RelationType == RelationType.CrossReference, ct))
                {
                    _db.TicketReferences.Add(new TicketReference { SourceId = source.Id, TargetId = target.Id, RelationType = RelationType.CrossReference, CreatedByEventId = evt.Id, CreatedAt = now });
                }
            }

            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);

        _logger.LogInformation("ReferenceWorker: event={EventId} mentions={Mentions} refs={Refs}", evt.Id, mentions.Count, references.Count);
    }

    private async Task<bool> AlreadyAsync(Guid ticketId, string type, Guid sourceEventId, Guid? subject, CancellationToken ct)
    {
        var candidates = await _db.TicketEvents.AsNoTracking()
            .Where(e => e.TicketId == ticketId && e.EventType == type && (subject == null || e.ActorId == subject))
            .OrderByDescending(e => e.Sequence).Take(200).ToListAsync(ct);
        var needle = sourceEventId.ToString();
        return candidates.Any(e => e.Payload.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }
}
