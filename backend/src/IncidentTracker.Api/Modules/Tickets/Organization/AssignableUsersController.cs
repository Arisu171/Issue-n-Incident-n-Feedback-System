using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Organization;

/// <summary>
/// Danh sách người có thể nhận ticket của một project.
///
/// ## Vì sao cần endpoint riêng
///
/// Ô chọn assignee trước đây gọi <c>GET /api/users</c>, mà endpoint đó đòi <c>user.read</c> —
/// **chỉ admin có**. Nút mở ô chọn thì lại mở theo <c>ticket.triage</c>. Kết quả: support và
/// responder — đúng nhóm làm việc với ticket hằng ngày — bấm vào ô chọn và nhận 403.
///
/// Đồng thời <c>GET /api/users</c> trả cả email, vai trò và trạng thái hoạt động của **mọi** tài
/// khoản. Ô chọn assignee chỉ cần tên và login, nên nới <c>user.read</c> ra cho cả nhóm triage là
/// trả giá quá đắt cho một danh sách gợi ý.
///
/// ## Vì sao có <c>{project}</c> trong đường dẫn dù chưa lọc theo project
///
/// Hiện vai trò là toàn cục nên danh sách chưa phụ thuộc project. Nhưng khi phân quyền theo
/// project được bật, kiểm tra quyền sẽ đọc <c>{project}</c> ngay trong đường dẫn — endpoint này
/// tự nhiên vào đúng phạm vi mà không phải đổi đường dẫn, tức không phải sửa cả phía gọi.
/// Slug vẫn được tra ngay từ bây giờ để project không tồn tại trả 404 thay vì trả danh sách rỗng
/// một cách khó hiểu.
/// </summary>
[ApiController]
[Route("api/projects/{project}")]
[Produces("application/json")]
public sealed class AssignableUsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly TicketQueries _q;

    public AssignableUsersController(AppDbContext db, TicketQueries q)
    {
        _db = db;
        _q = q;
    }

    /// <summary>Người đang hoạt động và có <c>ticket.triage</c> — cùng bộ quy tắc với TicketService.ResolveUsersAsync.</summary>
    [HttpGet("assignable-users")]
    [RequirePermission(Permissions.TicketTriage)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    [ProducesResponseType(typeof(IReadOnlyList<UserSummary>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<UserSummary>>> List(
        string project, [FromQuery] string? search, CancellationToken ct)
    {
        await _q.ProjectAsync(project, ct);

        var q = _db.Users.AsNoTracking()
            .Where(u => u.IsActive
                && u.UserRoles.Any(ur => ur.Role.RolePermissions
                    .Any(rp => rp.Permission.Code == Permissions.TicketTriage)));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            q = q.Where(u => u.Login.Contains(term) || u.DisplayName.ToLower().Contains(term));
        }

        // Cắt trần để một ô gợi ý không kéo về cả bảng user; người dùng gõ thêm để thu hẹp.
        return Ok(await q
            .OrderBy(u => u.DisplayName)
            .Take(50)
            .Select(u => new UserSummary(u.Id, u.Login, u.DisplayName))
            .ToListAsync(ct));
    }
}
