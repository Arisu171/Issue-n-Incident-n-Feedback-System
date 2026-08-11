using IncidentTracker.Api.Domain;

namespace IncidentTracker.Api.Modules.Incidents;

/// <summary>
/// ADR-002 / DRV-05 — bảng transition khai báo tập trung. Đây là <b>nơi duy nhất</b> mô tả
/// vòng đời sự cố; controller không được chứa <c>if</c> nào về trạng thái.
///
/// Ba trạng thái sinh ra chín cặp; đúng hai cặp hợp lệ, bảy cặp còn lại bị từ chối
/// bằng 409 (BR-BIZ-02, TC-BIZ-08).
/// </summary>
public static class IncidentStateMachine
{
    /// <summary>Trạng thái khởi tạo bắt buộc — client không được tự đặt (BR-BIZ-01).</summary>
    public const IncidentStatus InitialStatus = IncidentStatus.Investigating;

    private static readonly IReadOnlyDictionary<IncidentStatus, IncidentStatus?> Next =
        new Dictionary<IncidentStatus, IncidentStatus?>
        {
            [IncidentStatus.Investigating] = IncidentStatus.Mitigating,
            [IncidentStatus.Mitigating] = IncidentStatus.Resolved,
            [IncidentStatus.Resolved] = null // Release 1 không có Reopened (ADR-002, ISS-03).
        };

    /// <summary>Bước hợp lệ kế tiếp, hoặc null khi đã ở trạng thái cuối.</summary>
    public static IncidentStatus? AllowedNext(IncidentStatus current)
        => Next.TryGetValue(current, out var next) ? next : null;

    /// <summary>
    /// Chỉ chấp nhận đúng một bước tiến. Lùi trạng thái, nhảy bậc và chuyển trùng
    /// trạng thái đều trả false.
    /// </summary>
    public static bool CanTransition(IncidentStatus from, IncidentStatus to)
        => AllowedNext(from) == to;

    /// <summary>Permission bắt buộc cho từng bước chuyển (API-Incident-Status, FR-BIZ-03).</summary>
    public static string RequiredPermission(IncidentStatus to) => to switch
    {
        IncidentStatus.Resolved => Authorization.Permissions.IncidentResolve,
        _ => Authorization.Permissions.IncidentUpdateStatus
    };

    /// <summary>Toàn bộ 9 cặp trạng thái — dùng cho unit test ma trận 3×3 (TC-BIZ-08).</summary>
    public static IEnumerable<(IncidentStatus From, IncidentStatus To, bool Allowed)> AllPairs()
    {
        foreach (var from in Enum.GetValues<IncidentStatus>())
        {
            foreach (var to in Enum.GetValues<IncidentStatus>())
            {
                yield return (from, to, CanTransition(from, to));
            }
        }
    }
}
