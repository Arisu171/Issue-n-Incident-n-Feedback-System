using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Incidents;

namespace IncidentTracker.Api.Tests.Unit;

/// <summary>Ngưỡng cảnh báo SLA của mục 6.9 — "Investigating quá 24 giờ".</summary>
public class SlaEvaluatorTests
{
    private static readonly SlaOptions Options = new()
    {
        InvestigatingHours = 24,
        MitigatingHours = 48
    };

    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Investigating_chua_qua_24h_thi_khong_canh_bao()
    {
        var sla = SlaEvaluator.Evaluate(
            Options, IncidentStatus.Investigating, Now.AddHours(-23), null, Now);

        Assert.NotNull(sla);
        Assert.False(sla!.Breached);
        Assert.Equal(24, sla.ThresholdHours);
        Assert.Equal(23, sla.ElapsedHours);
        Assert.Equal(1, sla.RemainingHours);
    }

    [Fact]
    public void Investigating_qua_24h_thi_canh_bao()
    {
        var sla = SlaEvaluator.Evaluate(
            Options, IncidentStatus.Investigating, Now.AddHours(-25), null, Now);

        Assert.True(sla!.Breached);
        Assert.Equal(-1, sla.RemainingHours);
    }

    /// <summary>
    /// Đồng hồ của bước Mitigating tính từ mốc bước vào Mitigating, không phải từ lúc tạo —
    /// nếu tính sai, thời gian điều tra sẽ bị cộng dồn vào thời gian khắc phục.
    /// </summary>
    [Fact]
    public void Mitigating_tinh_gio_tu_moc_mitigating_at()
    {
        var createdAt = Now.AddHours(-100);
        var mitigatingAt = Now.AddHours(-10);

        var sla = SlaEvaluator.Evaluate(
            Options, IncidentStatus.Mitigating, createdAt, mitigatingAt, Now);

        Assert.False(sla!.Breached);
        Assert.Equal(10, sla.ElapsedHours);
        Assert.Equal(48, sla.ThresholdHours);
    }

    [Fact]
    public void Mitigating_qua_48h_thi_canh_bao()
    {
        var sla = SlaEvaluator.Evaluate(
            Options, IncidentStatus.Mitigating, Now.AddHours(-100), Now.AddHours(-49), Now);

        Assert.True(sla!.Breached);
    }

    /// <summary>Sự cố đã đóng thì ngừng tính giờ — resolved_at đã chốt số liệu.</summary>
    [Fact]
    public void Resolved_khong_con_dong_ho_SLA()
    {
        var sla = SlaEvaluator.Evaluate(
            Options, IncidentStatus.Resolved, Now.AddHours(-500), Now.AddHours(-400), Now);

        Assert.Null(sla);
    }

    [Fact]
    public void Nguong_doc_duoc_tu_cau_hinh_khong_hardcode()
    {
        var strict = new SlaOptions { InvestigatingHours = 1, MitigatingHours = 1 };

        var sla = SlaEvaluator.Evaluate(
            strict, IncidentStatus.Investigating, Now.AddHours(-2), null, Now);

        Assert.True(sla!.Breached);
        Assert.Equal(1, sla.ThresholdHours);
    }
}
