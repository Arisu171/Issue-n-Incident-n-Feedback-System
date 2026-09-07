using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Incidents;

namespace IncidentTracker.Api.Tests.Unit;

/// <summary>TCBIZ08 (TC-BIZ-08) — ma trận 3×3 các cặp trạng thái (ADR-002, BR-BIZ-02).</summary>
public class IncidentStateMachineTests
{
    [Theory]
    [InlineData(IncidentStatus.Investigating, IncidentStatus.Mitigating, true)]
    [InlineData(IncidentStatus.Mitigating, IncidentStatus.Resolved, true)]
    // Chuyển trùng trạng thái.
    [InlineData(IncidentStatus.Investigating, IncidentStatus.Investigating, false)]
    [InlineData(IncidentStatus.Mitigating, IncidentStatus.Mitigating, false)]
    [InlineData(IncidentStatus.Resolved, IncidentStatus.Resolved, false)]
    // Nhảy bậc.
    [InlineData(IncidentStatus.Investigating, IncidentStatus.Resolved, false)]
    // Lùi trạng thái.
    [InlineData(IncidentStatus.Mitigating, IncidentStatus.Investigating, false)]
    [InlineData(IncidentStatus.Resolved, IncidentStatus.Mitigating, false)]
    [InlineData(IncidentStatus.Resolved, IncidentStatus.Investigating, false)]
    public void TCBIZ08_CanTransition_chi_chap_nhan_dung_hai_cap(
        IncidentStatus from, IncidentStatus to, bool expected)
        => Assert.Equal(expected, IncidentStateMachine.CanTransition(from, to));

    [Fact]
    public void TCBIZ08_toan_bo_ma_tran_co_dung_2_cap_hop_le_tren_9()
    {
        var pairs = IncidentStateMachine.AllPairs().ToList();

        Assert.Equal(9, pairs.Count);
        Assert.Equal(2, pairs.Count(p => p.Allowed));
        Assert.Equal(7, pairs.Count(p => !p.Allowed));
    }

    [Fact]
    public void Trang_thai_khoi_tao_luon_la_Investigating()
        => Assert.Equal(IncidentStatus.Investigating, IncidentStateMachine.InitialStatus);

    [Theory]
    [InlineData(IncidentStatus.Investigating, IncidentStatus.Mitigating)]
    [InlineData(IncidentStatus.Mitigating, IncidentStatus.Resolved)]
    public void AllowedNext_tra_ve_dung_buoc_ke_tiep(IncidentStatus from, IncidentStatus expected)
        => Assert.Equal(expected, IncidentStateMachine.AllowedNext(from));

    [Fact]
    public void Resolved_la_trang_thai_cuoi_khong_co_buoc_ke_tiep()
        => Assert.Null(IncidentStateMachine.AllowedNext(IncidentStatus.Resolved));

    [Theory]
    [InlineData(IncidentStatus.Mitigating, Permissions.IncidentUpdateStatus)]
    [InlineData(IncidentStatus.Resolved, Permissions.IncidentResolve)]
    public void Buoc_dong_su_co_doi_permission_rieng(IncidentStatus to, string expected)
        => Assert.Equal(expected, IncidentStateMachine.RequiredPermission(to));
}
