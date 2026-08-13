using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IncidentTracker.Api.Common;

/// <summary>
/// Kiểm tra trước bằng <c>AnyAsync</c> rồi mới <c>SaveChanges</c> là một khoảng thời gian
/// chết (TOCTOU): hai request song song cùng vượt qua bước kiểm tra rồi cùng ghi, và request
/// thua cuộc nhận lỗi unique violation của PostgreSQL.
///
/// Ràng buộc ở tầng DB mới là thứ thực sự bảo vệ BR-01 và BR-03; phần kiểm tra trước chỉ để
/// có thông báo lỗi thân thiện. Vì vậy lỗi 23505 phải được dịch thành 409 hoặc thành thao tác
/// idempotent, chứ không được rò ra ngoài dưới dạng 500.
/// </summary>
public static class PostgresErrors
{
    /// <summary>SQLSTATE 23505 — unique_violation.</summary>
    public const string UniqueViolation = "23505";

    public static bool IsUniqueViolation(Exception exception)
        => exception is DbUpdateException { InnerException: PostgresException postgres }
           && postgres.SqlState == UniqueViolation;

    /// <summary>
    /// Ghi dữ liệu và dịch unique violation thành 409 kèm thông báo nghiệp vụ.
    /// </summary>
    public static async Task SaveTranslatingConflictAsync(
        this DbContext db, string conflictMessage, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            throw AppException.Conflict(conflictMessage);
        }
    }

    /// <summary>
    /// Ghi một bản ghi quan hệ. Nếu bản ghi đã tồn tại do request khác vừa chèn xong thì coi
    /// như thành công — đây chính là hành vi idempotent mà API contract cam kết cho
    /// <c>PUT</c> gán quan hệ.
    /// </summary>
    public static async Task SaveIgnoringDuplicateAsync(this DbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
        }
    }
}
