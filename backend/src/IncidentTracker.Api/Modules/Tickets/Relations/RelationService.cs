using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Relations;

public sealed class SubIssueRequest
{
    /// <summary><c>N</c>, <c>project#N</c> hoặc uuid.</summary>
    [Required] public string SubIssue { get; set; } = string.Empty;
}

public sealed class ReprioritizeRequest
{
    [Required] public string SubIssue { get; set; } = string.Empty;
    public string? AfterId { get; set; }
    public string? BeforeId { get; set; }
}

public sealed class DependencyRequest
{
    /// <summary>Ticket chặn (<c>N</c>, <c>project#N</c> hoặc uuid).</summary>
    [Required] public string Issue { get; set; } = string.Empty;
}

public sealed record SubIssueResponse(TicketRefResponse Ticket, int Position, SubIssuesSummary Summary);

/// <summary>
/// UC-10 / BR-REL-02, BR-REL-04 (Architecture v3.1): sub-issue (1 cha, ≤100 con, ≤8 cấp, không cycle,
/// cross-project được) và dependency BLOCKED_BY/BLOCKING (không cycle). Mọi thay đổi = cặp event
/// trên hai ticket trong cùng transaction.
/// </summary>
public sealed class RelationService
{
    public const int MaxSubIssues = 100;
    public const int MaxDepth = 8;
    public const int MaxBlockedBy = 50;

    private readonly AppDbContext _db;
    private readonly TicketQueries _q;
    private readonly TicketEventStore _events;
    private readonly TimeProvider _clock;

    public RelationService(AppDbContext db, TicketQueries q, TicketEventStore events, TimeProvider clock)
    {
        _db = db;
        _q = q;
        _events = events;
        _clock = clock;
    }

    // ---------------- sub-issues ----------------

    public async Task<IReadOnlyList<SubIssueResponse>> ListSubIssuesAsync(string slug, int number, ClaimsPrincipal user, CancellationToken ct)
    {
        var parent = await _q.LoadAsync(slug, number, false, ct);
        TicketAccess.EnsureCanRead(user, parent, parent.Project);
        var subs = await _q.WithDetails().Where(t => t.ParentTicketId == parent.Id).OrderBy(t => t.SubIssuePosition).ThenBy(t => t.CreatedAt).ToListAsync(ct);
        var list = new List<SubIssueResponse>();
        foreach (var s in subs)
        {
            list.Add(new SubIssueResponse(Ref(s), s.SubIssuePosition ?? 0, await SummaryAsync(s.Id, ct)));
        }
        return list;
    }

    public async Task<TicketRefResponse?> ParentAsync(string slug, int number, ClaimsPrincipal user, CancellationToken ct)
    {
        var child = await _q.LoadAsync(slug, number, false, ct);
        TicketAccess.EnsureCanRead(user, child, child.Project);
        return child.ParentTicket is null ? null : Ref(child.ParentTicket);
    }

    public async Task<IReadOnlyList<SubIssueResponse>> AddSubIssueAsync(string slug, int number, string subRef, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanTriage(user), "Sub-issue cần quyền Triage.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        await _q.InTransactionAsync(async () =>
        {
            var parentProbe = await _q.LoadAsync(slug, number, false, ct);
            var childProbe = await ResolveAsync(subRef, parentProbe.Project.Slug, ct);
            // Khoá theo thứ tự id để hai request chéo nhau không deadlock.
            foreach (var id in new[] { parentProbe.Id, childProbe.Id }.OrderBy(x => x)) await _q.LockTicketRowAsync(id, ct);
            var parent = await _q.LoadByIdAsync(parentProbe.Id, true, ct);
            var child = await _q.LoadByIdAsync(childProbe.Id, true, ct);
            TicketAccess.EnsureCanRead(user, child, child.Project);

            await AttachAsync(parent, child, actorId, now, ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return await ListSubIssuesAsync(slug, number, user, ct);
    }

    /// <summary>Dùng chung cho API và cho <c>CreateTicketRequest.ParentNumber</c> (đã ở trong transaction tạo ticket).</summary>
    public async Task AttachAsync(Ticket parent, Ticket child, Guid? actorId, DateTimeOffset now, CancellationToken ct)
    {
        if (parent.Id == child.Id) throw Unprocessable("Ticket không thể là sub-issue của chính nó.");
        if (child.ParentTicketId is not null)
        {
            throw Unprocessable(child.ParentTicketId == parent.Id ? "Ticket đã là sub-issue của ticket này." : "Ticket đã có ticket cha khác (BR-REL-04: đúng một cha).");
        }

        // BR-REL-02: cycle — đi lên từ parent; gặp child nghĩa là child đang là tổ tiên của parent.
        var ancestors = await AncestorsAsync(parent.Id, ct);
        if (ancestors.Contains(child.Id)) throw Unprocessable("Không thể gắn: sẽ tạo vòng lặp sub-issue (BR-REL-02).");

        // BR-REL-04: sâu ≤ 8 (depth(parent) tính từ gốc = 1) + chiều cao cây của child.
        var parentDepth = ancestors.Count + 1;           // gốc = 1
        var height = await HeightAsync(child.Id, ct);     // lá = 0
        if (parentDepth + 1 + height > MaxDepth) throw Unprocessable($"Vượt {MaxDepth} cấp lồng sub-issue (BR-REL-04).");

        var count = await _db.Tickets.CountAsync(t => t.ParentTicketId == parent.Id, ct);
        if (count >= MaxSubIssues) throw Unprocessable($"Ticket cha đã có {MaxSubIssues} sub-issue (BR-REL-04).");

        child.ParentTicketId = parent.Id;
        child.ParentTicket = parent;
        child.SubIssuePosition = (await _db.Tickets.Where(t => t.ParentTicketId == parent.Id).MaxAsync(t => (int?)t.SubIssuePosition, ct) ?? -1) + 1;

        await _events.AppendAsync(parent, TicketEventTypes.SubIssueAdded, new { sub_issue_id = child.Id, number = child.Number, project = child.Project.Slug, title = child.Title }, actorId, at: now, ct: ct);
        await _events.AppendAsync(child, TicketEventTypes.ParentIssueAdded, new { parent_ticket_id = parent.Id, number = parent.Number, project = parent.Project.Slug, title = parent.Title }, actorId, at: now, ct: ct);
    }

    public async Task RemoveSubIssueAsync(string slug, int number, string subRef, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanTriage(user), "Sub-issue cần quyền Triage.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        await _q.InTransactionAsync(async () =>
        {
            var parentProbe = await _q.LoadAsync(slug, number, false, ct);
            var childProbe = await ResolveAsync(subRef, parentProbe.Project.Slug, ct);
            foreach (var id in new[] { parentProbe.Id, childProbe.Id }.OrderBy(x => x)) await _q.LockTicketRowAsync(id, ct);
            var parent = await _q.LoadByIdAsync(parentProbe.Id, true, ct);
            var child = await _q.LoadByIdAsync(childProbe.Id, true, ct);
            if (child.ParentTicketId != parent.Id) throw AppException.NotFound("Ticket này không phải sub-issue của ticket cha.");

            child.ParentTicketId = null;
            child.ParentTicket = null;
            child.SubIssuePosition = null;
            await _events.AppendAsync(parent, TicketEventTypes.SubIssueRemoved, new { sub_issue_id = child.Id, number = child.Number, project = child.Project.Slug, title = child.Title }, actorId, at: now, ct: ct);
            await _events.AppendAsync(child, TicketEventTypes.ParentIssueRemoved, new { parent_ticket_id = parent.Id, number = parent.Number, project = parent.Project.Slug, title = parent.Title }, actorId, at: now, ct: ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    /// <summary>≈ <c>PATCH .../sub_issues/priority</c>: đặt sub-issue sau/trước một sub-issue khác.</summary>
    public async Task<IReadOnlyList<SubIssueResponse>> ReprioritizeAsync(string slug, int number, ReprioritizeRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanTriage(user), "Sắp xếp sub-issue cần quyền Triage.");
        if ((request.AfterId is null) == (request.BeforeId is null)) throw AppException.BadRequest("Cần đúng một trong after_id hoặc before_id.");

        await _q.InTransactionAsync(async () =>
        {
            var parent = await _q.LoadAsync(slug, number, false, ct);
            await _q.LockTicketRowAsync(parent.Id, ct);
            var subs = await _db.Tickets.Where(t => t.ParentTicketId == parent.Id).OrderBy(t => t.SubIssuePosition).ThenBy(t => t.CreatedAt).ToListAsync(ct);
            var moving = subs.FirstOrDefault(s => Matches(s, request.SubIssue, parent.Project.Slug)) ?? throw AppException.NotFound("Không tìm thấy sub-issue.");
            var anchor = subs.FirstOrDefault(s => Matches(s, request.AfterId ?? request.BeforeId!, parent.Project.Slug)) ?? throw AppException.NotFound("Không tìm thấy sub-issue mốc.");
            if (moving.Id == anchor.Id) return true;

            subs.Remove(moving);
            var idx = subs.IndexOf(anchor) + (request.AfterId is not null ? 1 : 0);
            subs.Insert(idx, moving);
            for (var i = 0; i < subs.Count; i++) subs[i].SubIssuePosition = i;
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return await ListSubIssuesAsync(slug, number, user, ct);
    }

    // ---------------- dependencies ----------------

    public async Task<IReadOnlyList<TicketRefResponse>> BlockedByAsync(string slug, int number, ClaimsPrincipal user, CancellationToken ct)
    {
        var ticket = await _q.LoadAsync(slug, number, false, ct);
        TicketAccess.EnsureCanRead(user, ticket, ticket.Project);
        return await (from r in _db.TicketReferences
                      join t in _db.Tickets.Include(x => x.Project) on r.TargetId equals t.Id
                      where r.SourceId == ticket.Id && r.RelationType == RelationType.BlockedBy
                      orderby r.CreatedAt
                      select new TicketRefResponse(t.Id, t.Project.Slug, t.Number, t.Title, t.State, t.StateReason)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketRefResponse>> BlockingAsync(string slug, int number, ClaimsPrincipal user, CancellationToken ct)
    {
        var ticket = await _q.LoadAsync(slug, number, false, ct);
        TicketAccess.EnsureCanRead(user, ticket, ticket.Project);
        return await (from r in _db.TicketReferences
                      join t in _db.Tickets.Include(x => x.Project) on r.SourceId equals t.Id
                      where r.TargetId == ticket.Id && r.RelationType == RelationType.BlockedBy
                      orderby r.CreatedAt
                      select new TicketRefResponse(t.Id, t.Project.Slug, t.Number, t.Title, t.State, t.StateReason)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TicketRefResponse>> AddBlockedByAsync(string slug, int number, string blockerRef, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanTriage(user), "Dependency cần quyền Triage.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        await _q.InTransactionAsync(async () =>
        {
            var blockedProbe = await _q.LoadAsync(slug, number, false, ct);
            var blockerProbe = await ResolveAsync(blockerRef, blockedProbe.Project.Slug, ct);
            if (blockedProbe.Id == blockerProbe.Id) throw Unprocessable("Ticket không thể tự chặn chính nó.");
            foreach (var id in new[] { blockedProbe.Id, blockerProbe.Id }.OrderBy(x => x)) await _q.LockTicketRowAsync(id, ct);
            var blocked = await _q.LoadByIdAsync(blockedProbe.Id, true, ct);
            var blocker = await _q.LoadByIdAsync(blockerProbe.Id, true, ct);
            TicketAccess.EnsureCanRead(user, blocker, blocker.Project);

            if (await _db.TicketReferences.AnyAsync(r => r.SourceId == blocked.Id && r.TargetId == blocker.Id && r.RelationType == RelationType.BlockedBy, ct))
                throw Unprocessable("Quan hệ blocked-by này đã tồn tại.");
            if (await _db.TicketReferences.CountAsync(r => r.SourceId == blocked.Id && r.RelationType == RelationType.BlockedBy, ct) >= MaxBlockedBy)
                throw Unprocessable($"Tối đa {MaxBlockedBy} ticket chặn một ticket.");

            // BR-REL-02: cycle trên đồ thị dependency — nếu từ blocker đi theo các cạnh "bị chặn bởi" tới được blocked → vòng.
            if (await ReachableViaBlockedByAsync(blocker.Id, blocked.Id, ct))
                throw Unprocessable("Không thể thêm: sẽ tạo vòng lặp dependency (BR-REL-02).");

            _db.TicketReferences.Add(new TicketReference { SourceId = blocked.Id, TargetId = blocker.Id, RelationType = RelationType.BlockedBy, CreatedAt = now });
            await _events.AppendAsync(blocked, TicketEventTypes.BlockedByAdded, new { blocker_ticket_id = blocker.Id, number = blocker.Number, project = blocker.Project.Slug, title = blocker.Title }, actorId, at: now, ct: ct);
            await _events.AppendAsync(blocker, TicketEventTypes.BlockingAdded, new { blocked_ticket_id = blocked.Id, number = blocked.Number, project = blocked.Project.Slug, title = blocked.Title }, actorId, at: now, ct: ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return await BlockedByAsync(slug, number, user, ct);
    }

    public async Task RemoveBlockedByAsync(string slug, int number, string blockerRef, ClaimsPrincipal user, CancellationToken ct)
    {
        TicketAccess.Ensure(TicketAccess.CanTriage(user), "Dependency cần quyền Triage.");
        var actorId = user.GetUserId();
        var now = _clock.GetUtcNow();

        await _q.InTransactionAsync(async () =>
        {
            var blockedProbe = await _q.LoadAsync(slug, number, false, ct);
            var blockerProbe = await ResolveAsync(blockerRef, blockedProbe.Project.Slug, ct);
            foreach (var id in new[] { blockedProbe.Id, blockerProbe.Id }.OrderBy(x => x)) await _q.LockTicketRowAsync(id, ct);
            var blocked = await _q.LoadByIdAsync(blockedProbe.Id, true, ct);
            var blocker = await _q.LoadByIdAsync(blockerProbe.Id, true, ct);

            var reference = await _db.TicketReferences.FirstOrDefaultAsync(r => r.SourceId == blocked.Id && r.TargetId == blocker.Id && r.RelationType == RelationType.BlockedBy, ct)
                ?? throw AppException.NotFound("Không có quan hệ blocked-by này.");
            _db.TicketReferences.Remove(reference);
            await _events.AppendAsync(blocked, TicketEventTypes.BlockedByRemoved, new { blocker_ticket_id = blocker.Id, number = blocker.Number, project = blocker.Project.Slug }, actorId, at: now, ct: ct);
            await _events.AppendAsync(blocker, TicketEventTypes.BlockingRemoved, new { blocked_ticket_id = blocked.Id, number = blocked.Number, project = blocked.Project.Slug }, actorId, at: now, ct: ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }, ct);
    }

    // ---------------- graph helpers ----------------

    private async Task<HashSet<Guid>> AncestorsAsync(Guid ticketId, CancellationToken ct)
    {
        var set = new HashSet<Guid>();
        var current = await _db.Tickets.IgnoreQueryFilters().Where(t => t.Id == ticketId).Select(t => t.ParentTicketId).FirstOrDefaultAsync(ct);
        while (current is { } id && set.Add(id))
        {
            current = await _db.Tickets.IgnoreQueryFilters().Where(t => t.Id == id).Select(t => t.ParentTicketId).FirstOrDefaultAsync(ct);
        }
        return set;
    }

    /// <summary>Chiều cao cây con (0 = lá). BFS theo mức, tối đa MaxDepth để không đi vô hạn.</summary>
    private async Task<int> HeightAsync(Guid rootId, CancellationToken ct)
    {
        var frontier = new List<Guid> { rootId };
        var height = 0;
        while (frontier.Count > 0 && height <= MaxDepth)
        {
            var next = await _db.Tickets.IgnoreQueryFilters().Where(t => t.ParentTicketId != null && frontier.Contains(t.ParentTicketId.Value)).Select(t => t.Id).ToListAsync(ct);
            if (next.Count == 0) break;
            height++;
            frontier = next;
        }
        return height;
    }

    private async Task<bool> ReachableViaBlockedByAsync(Guid from, Guid target, CancellationToken ct)
    {
        var visited = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(from);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == target) return true;
            if (!visited.Add(current)) continue;
            var blockers = await _db.TicketReferences.Where(r => r.SourceId == current && r.RelationType == RelationType.BlockedBy).Select(r => r.TargetId).ToListAsync(ct);
            foreach (var b in blockers) stack.Push(b);
        }
        return false;
    }

    private async Task<Ticket> ResolveAsync(string reference, string defaultSlug, CancellationToken ct)
        => Guid.TryParse(reference, out var id) ? await _q.LoadByIdAsync(id, true, ct) : await _q.ResolveRefAsync(reference, defaultSlug, ct);

    private static bool Matches(Ticket t, string reference, string defaultSlug)
    {
        if (Guid.TryParse(reference, out var id)) return t.Id == id;
        var text = reference.Trim().TrimStart('#');
        var hash = text.IndexOf('#');
        var slug = hash > 0 ? text[..hash] : defaultSlug;
        var numText = hash > 0 ? text[(hash + 1)..] : text;
        return int.TryParse(numText, out var n) && t.Number == n && string.Equals(t.Project?.Slug ?? defaultSlug, slug, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<SubIssuesSummary> SummaryAsync(Guid ticketId, CancellationToken ct)
    {
        var total = await _db.Tickets.CountAsync(t => t.ParentTicketId == ticketId, ct);
        var completed = await _db.Tickets.CountAsync(t => t.ParentTicketId == ticketId && t.State == TicketState.Closed, ct);
        return new SubIssuesSummary(total, completed, total == 0 ? 0 : (int)Math.Round(completed * 100.0 / total));
    }

    private static TicketRefResponse Ref(Ticket t) => new(t.Id, t.Project.Slug, t.Number, t.Title, t.State, t.StateReason);

    private static AppException Unprocessable(string detail) => new(StatusCodes.Status422UnprocessableEntity, "Quan hệ không hợp lệ", detail);
}

[ApiController]
[Route("api/projects/{project}/tickets/{number:int}")]
[Produces("application/json")]
public sealed class RelationsController : ControllerBase
{
    private readonly RelationService _relations;
    public RelationsController(RelationService relations) => _relations = relations;

    [HttpGet("sub_issues")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<SubIssueResponse>>> SubIssues(string project, int number, CancellationToken ct)
        => Ok(await _relations.ListSubIssuesAsync(project, number, User, ct));

    [HttpGet("parent")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<TicketRefResponse?>> Parent(string project, int number, CancellationToken ct)
    {
        var parent = await _relations.ParentAsync(project, number, User, ct);
        return parent is null ? NotFound() : Ok(parent);
    }

    [HttpPost("sub_issues")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<IReadOnlyList<SubIssueResponse>>> AddSubIssue(string project, int number, SubIssueRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _relations.AddSubIssueAsync(project, number, request.SubIssue, User, ct));

    [HttpDelete("sub_issues")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> RemoveSubIssue(string project, int number, SubIssueRequest request, CancellationToken ct)
    {
        await _relations.RemoveSubIssueAsync(project, number, request.SubIssue, User, ct);
        return NoContent();
    }

    [HttpPatch("sub_issues/priority")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<IReadOnlyList<SubIssueResponse>>> Reprioritize(string project, int number, ReprioritizeRequest request, CancellationToken ct)
        => Ok(await _relations.ReprioritizeAsync(project, number, request, User, ct));

    [HttpGet("dependencies/blocked_by")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<TicketRefResponse>>> BlockedBy(string project, int number, CancellationToken ct)
        => Ok(await _relations.BlockedByAsync(project, number, User, ct));

    [HttpGet("dependencies/blocking")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<TicketRefResponse>>> Blocking(string project, int number, CancellationToken ct)
        => Ok(await _relations.BlockingAsync(project, number, User, ct));

    [HttpPost("dependencies/blocked_by")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<IReadOnlyList<TicketRefResponse>>> AddBlockedBy(string project, int number, DependencyRequest request, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await _relations.AddBlockedByAsync(project, number, request.Issue, User, ct));

    [HttpDelete("dependencies/blocked_by")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> RemoveBlockedBy(string project, int number, DependencyRequest request, CancellationToken ct)
    {
        await _relations.RemoveBlockedByAsync(project, number, request.Issue, User, ct);
        return NoContent();
    }
}
