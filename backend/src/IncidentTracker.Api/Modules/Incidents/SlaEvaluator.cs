using IncidentTracker.Api.Domain;

namespace IncidentTracker.Api.Modules.Incidents;

/// <summary>Kết quả đánh giá SLA cho một sự cố, trả kèm trong IncidentResponse.</summary>
public sealed record SlaStatus(
    bool Breached,
    int? ThresholdHours,
    double ElapsedHours,
    double? RemainingHours);

/// <summary>
/// Quy tắc đo SLA nằm ở đúng một chỗ để endpoint danh sách, chi tiết và job cảnh báo nền
/// đều dùng chung một định nghĩa (tránh lặp lại sai lầm mà DRV-05 cảnh báo).
///
/// Mốc bắt đầu tính giờ là mốc bước vào trạng thái hiện tại, không phải lúc tạo sự cố —
/// nhờ vậy thời gian điều tra và thời gian khắc phục được đo tách bạch, đúng mục đích của
/// vòng đời ba bước (GOAL-BIZ-01).
/// </summary>
public static class SlaEvaluator
{
    /// <summary>Thời điểm sự cố bước vào trạng thái hiện tại.</summary>
    public static DateTimeOffset? EnteredCurrentStatusAt(
        IncidentStatus status, DateTimeOffset createdAt, DateTimeOffset? mitigatingAt)
        => status switch
        {
            IncidentStatus.Investigating => createdAt,
            IncidentStatus.Mitigating => mitigatingAt ?? createdAt,
            _ => null
        };

    public static SlaStatus? Evaluate(
        SlaOptions options,
        IncidentStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset? mitigatingAt,
        DateTimeOffset now)
    {
        var threshold = options.ThresholdFor(status);
        var since = EnteredCurrentStatusAt(status, createdAt, mitigatingAt);

        // Sự cố đã đóng thì ngừng tính giờ (mục 5 — resolved_at chốt SLA).
        if (threshold is null || since is null)
        {
            return null;
        }

        var elapsed = now - since.Value;
        var remaining = threshold.Value - elapsed;

        return new SlaStatus(
            Breached: elapsed > threshold.Value,
            ThresholdHours: (int)threshold.Value.TotalHours,
            ElapsedHours: Math.Round(elapsed.TotalHours, 2),
            RemainingHours: Math.Round(remaining.TotalHours, 2));
    }
}
