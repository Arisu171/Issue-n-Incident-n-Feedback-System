using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IncidentTracker.Api.Modules.Tickets.Organization;

/// <summary>Một project khách hàng có thể tham gia, kèm việc họ đã tham gia hay chưa.</summary>
public sealed record CatalogProjectResponse(string Slug, string Name, string? Description, bool Joined);

/// <summary>
/// Danh mục project cho khách hàng tự chọn.
///
/// Quản trị mở project cho vai trò <c>customer</c> bằng <c>project_role_access</c> — đó là **danh
/// mục**. Khách hàng tự nhận project mình có dùng dịch vụ — đó là <c>project_subscriptions</c>.
/// Quyền thật là <b>giao</b> của hai vế (xem <see cref="Permissions.SelfServiceRoles"/>), nên
/// endpoint này không cấp thêm quyền cho ai: tick một project chưa mở thì không tick được, và
/// tick rồi vẫn chỉ nhận đúng những quyền mà vai trò <c>customer</c> vốn có ở đó.
/// </summary>
public sealed class ProjectCatalogService
{
    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectCatalogService> _logger;

    public ProjectCatalogService(
        AppDbContext db, IMemoryCache cache, TimeProvider clock, ILogger<ProjectCatalogService> logger)
    {
        _db = db;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Project đang mở cho **vai trò tự phục vụ mà chính người này mang**.
    ///
    /// Không liệt kê project chưa mở cho họ: tên project là dữ liệu, và một danh sách đầy đủ mọi
    /// project kèm ô tick mờ là đã nói cho người ngoài biết hệ thống có những gì.
    /// </summary>
    private IQueryable<Project> CatalogFor(Guid userId)
        => _db.Projects.AsNoTracking()
            .Where(p => !p.IsArchived)
            .Where(p => _db.ProjectRoleAccess.Any(a =>
                a.ProjectId == p.Id
                && Permissions.SelfServiceRoles.Contains(a.Role.Name)
                && _db.UserRoles.Any(ur => ur.UserId == userId && ur.RoleId == a.RoleId)));

    public async Task<IReadOnlyList<CatalogProjectResponse>> ListAsync(Guid userId, CancellationToken ct)
        => await CatalogFor(userId)
            .OrderBy(p => p.Name)
            .Select(p => new CatalogProjectResponse(
                p.Slug,
                p.Name,
                p.Description,
                _db.ProjectSubscriptions.Any(s => s.UserId == userId && s.ProjectId == p.Id)))
            .ToListAsync(ct);

    public async Task JoinAsync(Guid userId, string slug, CancellationToken ct)
    {
        var projectId = await ResolveAsync(userId, slug, ct);

        // Idempotent: tick hai lần, hoặc hai tab cùng gửi, không được thành lỗi.
        if (await _db.ProjectSubscriptions.AnyAsync(s => s.UserId == userId && s.ProjectId == projectId, ct))
        {
            return;
        }

        _db.ProjectSubscriptions.Add(new ProjectSubscription
        {
            ProjectId = projectId,
            UserId = userId,
            JoinedAt = _clock.GetUtcNow()
        });
        await SaveAndRefreshAsync(userId, ct);
        _logger.LogInformation("Audit project.join actor={ActorId} project={Slug}", userId, slug);
    }

    public async Task LeaveAsync(Guid userId, string slug, CancellationToken ct)
    {
        var projectId = await ResolveAsync(userId, slug, ct);

        var row = await _db.ProjectSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId && s.ProjectId == projectId, ct);
        if (row is null)
        {
            return;
        }

        _db.ProjectSubscriptions.Remove(row);
        await SaveAndRefreshAsync(userId, ct);
        _logger.LogInformation("Audit project.leave actor={ActorId} project={Slug}", userId, slug);
    }

    /// <summary>
    /// 404 cho project ngoài danh mục — kể cả khi nó có thật. Trả 403 ở đây là xác nhận project
    /// đó tồn tại, đúng thứ mà việc lọc danh sách ở trên vừa cố giữ kín.
    /// </summary>
    private async Task<Guid> ResolveAsync(Guid userId, string slug, CancellationToken ct)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        var id = await CatalogFor(userId)
            .Where(p => p.Slug == normalized)
            .Select(p => (Guid?)p.Id)
            .FirstOrDefaultAsync(ct);

        return id ?? throw AppException.NotFound($"Không có project '{slug}' trong danh mục tham gia được.");
    }

    /// <summary>
    /// Ảnh chụp quyền được cache theo người dùng, nên đổi xong phải xoá cache của **chính họ**;
    /// nếu không, ô tick đã đổi mà màn hình vẫn báo 403 cho tới khi cache hết hạn.
    /// </summary>
    private async Task SaveAndRefreshAsync(Guid userId, CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);
        _cache.Remove(PrincipalEnrichmentMiddleware.CacheKey(userId));
    }
}

/// <summary>
/// Đường dẫn <b>không</b> mang <c>{project}</c>, và đó là điều bắt buộc: người dùng chưa tham gia
/// project thì chưa có quyền nào trong đó, nên một phép kiểm quyền theo project sẽ chặn đúng cái
/// thao tác dùng để vào. Bù lại, mọi thứ ở đây chỉ đụng tới dữ liệu của **chính người gọi**.
/// </summary>
[ApiController]
[Route("api/project-catalog")]
[Produces("application/json")]
public sealed class ProjectCatalogController : ControllerBase
{
    private readonly ProjectCatalogService _catalog;

    public ProjectCatalogController(ProjectCatalogService catalog) => _catalog = catalog;

    /// <summary>Project mở cho vai trò của tôi, kèm cờ đã tham gia hay chưa.</summary>
    [HttpGet]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    [ProducesResponseType(typeof(IReadOnlyList<CatalogProjectResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CatalogProjectResponse>>> List(CancellationToken ct)
        => Ok(await _catalog.ListAsync(User.GetUserId(), ct));

    /// <summary>Tham gia một project trong danh mục.</summary>
    [HttpPut("{slug}")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Join(string slug, CancellationToken ct)
    {
        await _catalog.JoinAsync(User.GetUserId(), slug, ct);
        return NoContent();
    }

    /// <summary>Rời một project. Dữ liệu đã gửi vẫn còn, chỉ là không xem được nữa.</summary>
    [HttpDelete("{slug}")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Leave(string slug, CancellationToken ct)
    {
        await _catalog.LeaveAsync(User.GetUserId(), slug, ct);
        return NoContent();
    }
}
