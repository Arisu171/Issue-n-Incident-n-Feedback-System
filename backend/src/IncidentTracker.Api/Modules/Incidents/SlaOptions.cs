using System.ComponentModel.DataAnnotations;
using IncidentTracker.Api.Domain;

namespace IncidentTracker.Api.Modules.Incidents;

/// <summary>
/// Ngưỡng SLA của mục 6.9 ("cảnh báo khi có sự cố ở Investigating quá 24 giờ") và thuật ngữ
/// SLA trong README. Ngưỡng là giả định ban đầu, chỉnh được qua biến môi trường mà không sửa mã.
/// </summary>
public sealed class SlaOptions
{
    public const string SectionName = "Sla";

    /// <summary>Số giờ tối đa một sự cố được phép ở <c>Investigating</c>.</summary>
    [Range(1, 720)]
    public int InvestigatingHours { get; set; } = 24;

    /// <summary>Số giờ tối đa một sự cố được phép ở <c>Mitigating</c>.</summary>
    [Range(1, 720)]
    public int MitigatingHours { get; set; } = 48;

    /// <summary>Chu kỳ quét nền để ghi cảnh báo. Đặt 0 để tắt hẳn job.</summary>
    [Range(0, 1440)]
    public int ScanIntervalMinutes { get; set; } = 15;

    /// <summary>Ngưỡng tương ứng với trạng thái hiện tại; <c>Resolved</c> không còn tính giờ.</summary>
    public TimeSpan? ThresholdFor(IncidentStatus status) => status switch
    {
        IncidentStatus.Investigating => TimeSpan.FromHours(InvestigatingHours),
        IncidentStatus.Mitigating => TimeSpan.FromHours(MitigatingHours),
        _ => null
    };
}
