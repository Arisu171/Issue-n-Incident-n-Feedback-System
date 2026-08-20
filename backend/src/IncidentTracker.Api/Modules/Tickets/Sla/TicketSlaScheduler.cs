using System.ComponentModel.DataAnnotations;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Observability;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Tickets.Sla;

public sealed record SlaPolicyResponse(Guid Id, TicketPriority Priority, int ResponseTimeMinutes, int ResolutionTimeMinutes, int EscalateAfterMinutes, bool IsActive);

public sealed class SlaPolicyRequest
{
    [Range(0, 1_000_000)] public int ResponseTimeMinutes { get; set; }
    [Range(0, 10_000_000)] public int ResolutionTimeMinutes { get; set; }
    [Range(0, 1_000_000)] public int EscalateAfterMinutes { get; set; } = 60;
    public bool IsActive { get; set; } = true;
}

/// <summary>Mở rộng riêng (nhóm B) — UC-16 / 6.4: quét idempotent, không dùng in-memory timer cho từng ticket.</summary>
public sealed class TicketSlaScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<TicketingOptions> _options;
    private readonly AppMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly ILogger<TicketSlaScheduler> _logger;

    public TicketSlaScheduler(IServiceScopeFactory scopes, IOptionsMonitor<TicketingOptions> options, AppMetrics metrics, TimeProvider clock, ILogger<TicketSlaScheduler> logger)
    {
        _scopes = scopes;
        _options = options;
        _metrics = metrics;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = _options.CurrentValue.Sla.ScanIntervalMinutes;
        if (minutes <= 0)
        {
            _logger.LogInformation("TicketSlaScheduler đã tắt (Ticketing:Sla:ScanIntervalMinutes = 0).");
            return;
        }
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        do
        {
            try { await ScanAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "TicketSlaScheduler quét thất bại; thử lại ở chu kỳ sau."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Một lượt quét. Idempotent: mỗi mốc (WARNING/BREACHED/ESCALATED) chỉ ghi khi chưa có event tương ứng.</summary>
    internal async Task<(int Warned, int Breached, int Escalated)> ScanAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var q = scope.ServiceProvider.GetRequiredService<TicketQueries>();
        var events = scope.ServiceProvider.GetRequiredService<TicketEventStore>();
        var now = _clock.GetUtcNow();

        var policies = await db.SlaPolicies.AsNoTracking().Where(p => p.IsActive).ToDictionaryAsync(p => p.Priority, ct);
        var candidates = await db.Tickets.AsNoTracking()
            .Where(t => t.State == TicketState.Open && t.SlaDueAt != null && t.FirstResponseAt == null && t.Priority != null)
            .Select(t => new { t.Id, t.Priority, t.SlaDueAt, t.CreatedAt }).ToListAsync(ct);

        var adminLogins = await db.Users.AsNoTracking().Where(u => u.UserRoles.Any(r => r.Role.Name == "admin") && u.IsActive).Select(u => u.Login).ToListAsync(ct);
        int warned = 0, breached = 0, escalated = 0, breachedTotal = 0;

        foreach (var c in candidates)
        {
            if (!policies.TryGetValue(c.Priority!.Value, out var policy)) continue;
            var due = c.SlaDueAt!.Value;
            var window = TimeSpan.FromMinutes(policy.ResponseTimeMinutes);
            var warnAt = due - TimeSpan.FromTicks((long)(window.Ticks * 0.2));
            var escalateAt = due + TimeSpan.FromMinutes(policy.EscalateAfterMinutes);
            if (now >= due) breachedTotal++;

            string? needed = now >= escalateAt ? TicketEventTypes.Escalated : now >= due ? TicketEventTypes.SlaBreached : now >= warnAt ? TicketEventTypes.SlaWarning : null;
            if (needed is null) continue;

            var existing = await db.TicketEvents.AsNoTracking().Where(e => e.TicketId == c.Id && (e.EventType == TicketEventTypes.SlaWarning || e.EventType == TicketEventTypes.SlaBreached || e.EventType == TicketEventTypes.Escalated))
                .Select(e => e.EventType).ToListAsync(ct);

            var toWrite = new List<string>();
            if (now >= warnAt && !existing.Contains(TicketEventTypes.SlaWarning)) toWrite.Add(TicketEventTypes.SlaWarning);
            if (now >= due && !existing.Contains(TicketEventTypes.SlaBreached)) toWrite.Add(TicketEventTypes.SlaBreached);
            if (now >= escalateAt && !existing.Contains(TicketEventTypes.Escalated)) toWrite.Add(TicketEventTypes.Escalated);
            if (toWrite.Count == 0) continue;

            await q.InTransactionAsync(async () =>
            {
                await q.LockTicketRowAsync(c.Id, ct);
                var ticket = await q.LoadByIdAsync(c.Id, true, ct);
                if (ticket.State != TicketState.Open || ticket.FirstResponseAt is not null) return false;
                foreach (var type in toWrite)
                {
                    object payload = type == TicketEventTypes.Escalated
                        ? new { policy_id = policy.Id, due_at = due, escalated_to = adminLogins, overdue_minutes = (int)(now - due).TotalMinutes }
                        : new { policy_id = policy.Id, due_at = due, priority = EnumNaming.Format(c.Priority.Value) };
                    await events.AppendAsync(ticket, type, payload, null, at: now, ct: ct);
                    if (type == TicketEventTypes.SlaWarning) warned++;
                    else if (type == TicketEventTypes.SlaBreached) breached++;
                    else escalated++;
                }
                await db.SaveChangesAsync(ct);
                return true;
            }, ct);
        }

        _metrics.SetTicketSlaBreached(breachedTotal);
        if (warned + breached + escalated > 0)
        {
            _logger.LogWarning("SLA scan: warning={Warned} breached={Breached} escalated={Escalated} (đang quá hạn: {Total})", warned, breached, escalated, breachedTotal);
        }
        return (warned, breached, escalated);
    }
}

[ApiController]
[Route("api/sla-policies")]
[Produces("application/json")]
public sealed class SlaPoliciesController : ControllerBase
{
    private readonly AppDbContext _db;
    public SlaPoliciesController(AppDbContext db) => _db = db;

    [HttpGet]
    [RequireGlobalPermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<SlaPolicyResponse>>> List(CancellationToken ct)
        => Ok((await _db.SlaPolicies.AsNoTracking().OrderBy(p => p.Priority).ToListAsync(ct))
            .Select(p => new SlaPolicyResponse(p.Id, p.Priority, p.ResponseTimeMinutes, p.ResolutionTimeMinutes, p.EscalateAfterMinutes, p.IsActive)).ToList());

    /// <summary>Đặt policy cho một priority (upsert) — Admin.</summary>
    [HttpPut("{priority}")]
    [RequireGlobalPermission(Permissions.SlaManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<SlaPolicyResponse>> Put(string priority, SlaPolicyRequest request, CancellationToken ct)
    {
        if (!EnumNaming.TryParse<TicketPriority>(priority, out var pr)) throw AppException.BadRequest("priority phải là P0..P3.");
        var policy = await _db.SlaPolicies.FirstOrDefaultAsync(p => p.Priority == pr, ct);
        if (policy is null)
        {
            policy = new SlaPolicy { Id = Guid.NewGuid(), Priority = pr };
            _db.SlaPolicies.Add(policy);
        }
        policy.ResponseTimeMinutes = request.ResponseTimeMinutes;
        policy.ResolutionTimeMinutes = request.ResolutionTimeMinutes;
        policy.EscalateAfterMinutes = request.EscalateAfterMinutes;
        policy.IsActive = request.IsActive;
        await _db.SaveChangesAsync(ct);
        return Ok(new SlaPolicyResponse(policy.Id, policy.Priority, policy.ResponseTimeMinutes, policy.ResolutionTimeMinutes, policy.EscalateAfterMinutes, policy.IsActive));
    }
}
