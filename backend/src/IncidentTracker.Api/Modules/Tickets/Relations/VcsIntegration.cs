using System.ComponentModel.DataAnnotations;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Relations;

/// <summary>Sự kiện từ hệ VCS (PR/commit) — BR-REL-06.</summary>
public sealed class VcsEventRequest
{
    /// <summary><c>pull_request</c> | <c>commit</c></summary>
    [Required] public string Kind { get; set; } = string.Empty;
    [Required, MaxLength(2000)] public string Url { get; set; } = string.Empty;
    [MaxLength(64)] public string? Sha { get; set; }
    [MaxLength(256)] public string? Title { get; set; }
    /// <summary>Commit message hoặc PR body — nơi chứa closing keyword / #N.</summary>
    [MaxLength(65536)] public string? Message { get; set; }
    /// <summary>PR đã merge / commit đã vào nhánh mặc định → closing keyword có hiệu lực.</summary>
    public bool Merged { get; set; }
    public bool OnDefaultBranch { get; set; }
    /// <summary>Định danh PR (số) để CONNECTED/DISCONNECTED đối chiếu.</summary>
    public string? PrId { get; set; }
}

public sealed record VcsEventResult(IReadOnlyList<TicketRefResponse> Connected, IReadOnlyList<TicketRefResponse> Referenced, IReadOnlyList<TicketRefResponse> Closed);

/// <summary>
/// BR-REL-06 (Architecture v3.1): <c>closes/fixes/resolves #N</c> trong PR → <c>CONNECTED</c> ngay; khi merge
/// vào nhánh mặc định → <c>CLOSED</c> (actor = hệ thống, kèm <c>commit_sha</c>). Commit chỉ nhắc <c>#N</c> → <c>REFERENCED</c>.
/// </summary>
public sealed class VcsIntegrationService
{
    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly TicketEventStore _events;
    private readonly TicketService _tickets;
    private readonly TimeProvider _clock;

    public VcsIntegrationService(AppDbContext db, TicketQueries q, TicketEventStore events, TicketService tickets, TimeProvider clock)
    {
        _db = db;
        _q = q;
        _events = events;
        _tickets = tickets;
        _clock = clock;
    }

    public async Task<VcsEventResult> HandleAsync(string slug, VcsEventRequest request, CancellationToken ct)
    {
        var kind = request.Kind.Trim().ToLowerInvariant();
        if (kind is not ("pull_request" or "commit")) throw AppException.BadRequest("kind phải là pull_request hoặc commit.");
        var text = string.Join("\n", request.Title, request.Message);
        var closing = MarkdownRenderer.ExtractClosingReferences(text).Select(r => (r.ProjectSlug ?? slug, r.Number)).Distinct().ToList();
        var all = MarkdownRenderer.ExtractReferences(text).Select(r => (r.ProjectSlug ?? slug, r.Number)).Distinct().ToList();
        var now = _clock.GetUtcNow();

        var connected = new List<TicketRefResponse>();
        var referenced = new List<TicketRefResponse>();
        var closed = new List<TicketRefResponse>();

        foreach (var (projectSlug, number) in all)
        {
            await _q.InTransactionAsync(async () =>
            {
                var probe = await _q.WithDetails().FirstOrDefaultAsync(t => t.Project.Slug == projectSlug && t.Number == number, ct);
                if (probe is null) return false;
                await _q.LockTicketRowAsync(probe.Id, ct);
                var ticket = await _q.LoadByIdAsync(probe.Id, true, ct);
                var isClosing = closing.Contains((projectSlug, number));
                var reference = new TicketRefResponse(ticket.Id, ticket.Project.Slug, ticket.Number, ticket.Title, ticket.State, ticket.StateReason);

                if (kind == "pull_request")
                {
                    if (isClosing)
                    {
                        await _events.AppendAsync(ticket, TicketEventTypes.Connected, new { pr_id = request.PrId, pr_url = request.Url, title = request.Title }, null, at: now, ct: ct);
                        connected.Add(reference);
                    }
                    else
                    {
                        await _events.AppendAsync(ticket, TicketEventTypes.CrossReferenced, new { source_kind = "pull_request", pr_id = request.PrId, pr_url = request.Url, title = request.Title }, null, at: now, ct: ct);
                        referenced.Add(reference);
                    }
                }
                else
                {
                    await _events.AppendAsync(ticket, TicketEventTypes.Referenced, new { commit_sha = request.Sha, commit_url = request.Url, message = request.Title }, null, at: now, ct: ct);
                    referenced.Add(reference);
                }

                var effective = kind == "pull_request" ? request.Merged : request.OnDefaultBranch;
                if (isClosing && effective && ticket.State == TicketState.Open)
                {
                    await _tickets.CloseAsync(ticket, StateReason.Completed, null, null, now, null, ct, commitSha: request.Sha);
                    closed.Add(reference with { State = TicketState.Closed, StateReason = StateReason.Completed });
                }

                await _db.SaveChangesAsync(ct);
                return true;
            }, ct);
        }

        return new VcsEventResult(connected, referenced, closed);
    }
}

[ApiController]
[Route("api/projects/{project}/vcs")]
[Produces("application/json")]
public sealed class VcsIntegrationController : ControllerBase
{
    private readonly VcsIntegrationService _vcs;
    public VcsIntegrationController(VcsIntegrationService vcs) => _vcs = vcs;

    /// <summary>Nhận sự kiện PR/commit (Admin — thường do hệ tích hợp gọi với tài khoản dịch vụ).</summary>
    [HttpPost("events")]
    [RequirePermission(Permissions.WebhookManage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<VcsEventResult>> Receive(string project, VcsEventRequest request, CancellationToken ct)
        => Ok(await _vcs.HandleAsync(project, request, ct));
}
