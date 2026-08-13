using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using System.Security.Claims;

namespace IncidentTracker.Api.Modules.Tickets.Organization;

/// <summary>
/// Project ảo <c>uncategorized</c> — nơi chứa dữ liệu chưa gắn project nào.
///
/// ## Vì sao là ảo, không phải một hàng trong bảng
///
/// Một hàng thật thì sửa được tên, xoá được, gán được contact link, và sớm muộn sẽ có người làm
/// đúng những việc đó — rồi dữ liệu chưa phân loại biến mất hoặc đổi nghĩa. Ở đây nó chỉ là một
/// tên gọi cho điều kiện <c>ProjectId IS NULL</c>, nên không ai xoá được, và cũng không cần bảo
/// vệ nó bằng cờ hay ràng buộc nào.
///
/// ## Ai thấy nó
///
/// Người có vai trò toàn cục (<c>admin</c>), hoặc có vai trò <c>manager</c> ở **bất kỳ** project
/// nào — họ chính là người đi phân loại. Đây là ngoại lệ duy nhất không đi qua bảng cấp quyền,
/// vì <c>uncategorized</c> không có hàng nào để mà cấp.
/// </summary>
public static class VirtualProject
{
    public const string Uncategorized = "uncategorized";

    /// <summary>Slug này có phải project ảo không (không phân biệt hoa/thường).</summary>
    public static bool IsUncategorized(string? slug)
        => string.Equals(slug, Uncategorized, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Chặn tạo project trùng tên với project ảo: hai thứ khác nhau cùng tên trên một thanh chọn
    /// là chỗ người dùng chắc chắn bấm nhầm.
    /// </summary>
    public static void EnsureNotReserved(string slug)
    {
        if (IsUncategorized(slug))
        {
            throw AppException.Conflict(
                $"'{Uncategorized}' là tên dành riêng cho mục chứa dữ liệu chưa phân loại.");
        }
    }

    /// <summary>
    /// Người này có được xem mục chưa phân loại không.
    ///
    /// Kiểm bằng permission chứ không bằng tên vai trò: <c>project.member.manage</c> là thứ
    /// manager có mà support/responder không, và vai trò tự tạo mang quyền đó cũng nên thấy.
    /// </summary>
    public static bool CanSee(ClaimsPrincipal user)
        => user.HasGlobalPermission(Permissions.ProjectManage)
           || user.HasPermission(Permissions.ProjectMemberManage);

    /// <summary>
    /// Đổi slug trên đường dẫn thành <c>ProjectId</c> để lọc dữ liệu.
    ///
    /// Trả <c>null</c> cho <c>uncategorized</c> — đó chính là điều kiện lọc
    /// <c>ProjectId IS NULL</c>. Project không tồn tại thì 404, và mục chưa phân loại mà người
    /// gọi không được xem thì 403.
    /// </summary>
    public static async Task<Guid?> ResolveAsync(
        Persistence.AppDbContext db, string slug, ClaimsPrincipal user, CancellationToken ct)
    {
        if (IsUncategorized(slug))
        {
            if (!CanSee(user))
            {
                throw AppException.Forbidden(
                    "Mục chưa phân loại chỉ dành cho quản trị viên và người quản lý project.");
            }
            return null;
        }

        var normalized = slug.Trim().ToLowerInvariant();
        var id = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(db.Projects.Where(p => p.Slug == normalized).Select(p => (Guid?)p.Id), ct);

        return id ?? throw AppException.NotFound($"Không tìm thấy project '{slug}'.");
    }
}
