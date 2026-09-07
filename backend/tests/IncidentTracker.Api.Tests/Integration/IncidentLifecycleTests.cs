using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>TC-BIZ-01 … TC-BIZ-05, TC-BIZ-09 và TC-NFR-02 — vòng đời sự cố.</summary>
[Collection(ApiCollection.Name)]
public class IncidentLifecycleTests
{
    private readonly ApiFixture _fx;

    public IncidentLifecycleTests(ApiFixture fx) => _fx = fx;

    // ---------- TC-BIZ-01 · US-BIZ-01 ----------

    [Fact]
    public async Task TCBIZ01_tao_su_co_luon_o_Investigating_va_reporter_lay_tu_JWT()
    {
        var support = await _fx.SupportAsync();
        var me = await support.GetFromJsonAsync<MeDto>("/api/auth/me", ApiFactory.Json);

        var response = await support.PostAsJsonAsync("/api/projects/support/incidents",
            new { title = "API thanh toán trả 502", description = "Lỗi 502 khi gọi /pay", severity = "High" },
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var incident = await response.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json);

        Assert.Equal("Investigating", incident!.Status);
        Assert.Equal("Mitigating", incident.AllowedNextStatus);
        Assert.Equal(me!.Id, incident.Reporter.Id);
        Assert.Null(incident.ResolvedAt);
        Assert.Null(incident.MitigatingAt);
    }

    /// <summary>US-BIZ-01/AC-02 — client gửi kèm status = Resolved thì giá trị đó bị bỏ qua.</summary>
    [Fact]
    public async Task TCBIZ01_client_tu_dat_status_bi_bo_qua()
    {
        var support = await _fx.SupportAsync();

        var response = await support.PostAsJsonAsync("/api/projects/support/incidents",
            new
            {
                title = "Client cố đặt trạng thái",
                description = "Body có kèm status và reporterId",
                severity = "Low",
                status = "Resolved",
                reporterId = Guid.NewGuid(),
                isDeleted = true
            }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var incident = await response.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json);

        Assert.Equal("Investigating", incident!.Status);
        Assert.False(incident.IsDeleted);
        // Severity Low là CLR default của enum — kiểm tra nó không bị DB default ghi đè thành Medium.
        Assert.Equal("Low", incident.Severity);
    }

    /// <summary>US-BIZ-01/AC-03 — title rỗng trả 400 ProblemDetails và không tạo bản ghi.</summary>
    [Fact]
    public async Task TCBIZ01_thieu_tieu_de_tra_400()
    {
        var support = await _fx.SupportAsync();
        var before = await CountIncidentsAsync();

        var response = await support.PostAsJsonAsync("/api/projects/support/incidents",
            new { title = "", description = "Không có tiêu đề" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.True(problem.TryGetProperty("errors", out _));
        Assert.True(problem.TryGetProperty("correlationId", out _));
        Assert.Equal(before, await CountIncidentsAsync());
    }

    [Fact]
    public async Task Support_khong_co_quyen_chuyen_trang_thai_tra_403()
    {
        var support = await _fx.SupportAsync();
        var incident = await CreateIncidentAsync(support, "Support thử chuyển trạng thái");

        var response = await support.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- TC-BIZ-02 · US-BIZ-02 ----------

    [Fact]
    public async Task TCBIZ02_chuyen_sang_Mitigating_thanh_cong_va_ghi_lich_su()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var incident = await CreateIncidentAsync(support, "Chuyển sang Mitigating");

        var response = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating", note = "Đã tìm ra nguyên nhân" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json);

        Assert.Equal("Mitigating", updated!.Status);
        Assert.Equal("Resolved", updated.AllowedNextStatus);
        Assert.NotNull(updated.MitigatingAt);

        var history = await responder.GetFromJsonAsync<List<HistoryDto>>(
            $"/api/projects/support/incidents/{incident.Id}/history", ApiFactory.Json);
        Assert.Single(history!);
        Assert.Equal("Investigating", history![0].FromStatus);
        Assert.Equal("Mitigating", history[0].ToStatus);
        Assert.Equal("Đã tìm ra nguyên nhân", history[0].Note);
    }

    /// <summary>US-BIZ-03/AC-02 — nhảy bậc Investigating → Resolved trả 409 kèm bước bắt buộc.</summary>
    [Fact]
    public async Task TCBIZ02_nhay_bac_tra_409_kem_allowedNextStatus()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var incident = await CreateIncidentAsync(support, "Nhảy bậc trạng thái");

        var response = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Resolved" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("Investigating", problem.GetProperty("currentStatus").GetString());
        Assert.Equal("Mitigating", problem.GetProperty("allowedNextStatus").GetString());

        var after = await responder.GetFromJsonAsync<IncidentDto>(
            $"/api/projects/support/incidents/{incident.Id}", ApiFactory.Json);
        Assert.Equal("Investigating", after!.Status);
        Assert.Empty(await GetHistoryAsync(responder, incident.Id));
    }

    [Fact]
    public async Task TCBIZ02_chuyen_trung_trang_thai_tra_409()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var incident = await CreateIncidentAsync(support, "Chuyển trùng trạng thái");

        var response = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Investigating" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TCBIZ02_lui_trang_thai_tra_409()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var incident = await CreateIncidentAsync(support, "Lùi trạng thái");
        await TransitionAsync(responder, incident.Id, "Mitigating");

        var response = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Investigating" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---------- TC-BIZ-03 · US-BIZ-03 ----------

    [Fact]
    public async Task TCBIZ03_dong_su_co_ghi_resolvedAt_va_resolvedBy()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var me = await responder.GetFromJsonAsync<MeDto>("/api/auth/me", ApiFactory.Json);

        var incident = await CreateIncidentAsync(support, "Đóng sự cố hợp lệ");
        await TransitionAsync(responder, incident.Id, "Mitigating");

        var response = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Resolved", note = "Đã vá xong" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var resolved = await response.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json);

        Assert.Equal("Resolved", resolved!.Status);
        Assert.Null(resolved.AllowedNextStatus);
        Assert.NotNull(resolved.ResolvedAt);
        Assert.Equal(me!.Id, resolved.Resolver!.Id);
    }

    /// <summary>
    /// US-BIZ-03/AC-03 — có <c>incident.update_status</c> nhưng thiếu <c>incident.resolve</c>
    /// thì bị chặn 403 <b>trước khi</b> vào thân controller, và không sinh dòng lịch sử nào.
    /// </summary>
    [Fact]
    public async Task TCBIZ03_thieu_quyen_resolve_tra_403_va_khong_ghi_lich_su()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var mitigator = await _fx.MitigatorAsync();

        var incident = await CreateIncidentAsync(support, "Thiếu quyền đóng sự cố");
        await TransitionAsync(responder, incident.Id, "Mitigating");
        var historyBefore = await GetHistoryAsync(responder, incident.Id);

        var response = await mitigator.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Resolved" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("incident.resolve", problem.GetProperty("requiredPermission").GetString());

        var after = await responder.GetFromJsonAsync<IncidentDto>(
            $"/api/projects/support/incidents/{incident.Id}", ApiFactory.Json);
        Assert.Equal("Mitigating", after!.Status);
        Assert.Equal(historyBefore.Count, (await GetHistoryAsync(responder, incident.Id)).Count);
    }

    /// <summary>Cùng người đó vẫn chuyển sang Mitigating được — chứng minh filter chỉ chặn bước Resolved.</summary>
    [Fact]
    public async Task Mitigator_van_chuyen_duoc_sang_Mitigating()
    {
        var support = await _fx.SupportAsync();
        var mitigator = await _fx.MitigatorAsync();
        var incident = await CreateIncidentAsync(support, "Mitigator chuyển bước một");

        var response = await mitigator.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>US-BIZ-03/AC-05 — đóng lại sự cố đã đóng trả 409 và resolvedAt giữ nguyên.</summary>
    [Fact]
    public async Task TCBIZ03_goi_lai_lan_hai_tra_409_va_giu_nguyen_resolvedAt()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var incident = await CreateIncidentAsync(support, "Đóng lại lần hai");

        await TransitionAsync(responder, incident.Id, "Mitigating");
        var first = await TransitionAsync(responder, incident.Id, "Resolved");

        var second = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Resolved" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var after = await responder.GetFromJsonAsync<IncidentDto>(
            $"/api/projects/support/incidents/{incident.Id}", ApiFactory.Json);
        Assert.Equal(first.ResolvedAt, after!.ResolvedAt);
    }

    /// <summary>US-BIZ-03/AC-04.</summary>
    [Fact]
    public async Task TCBIZ03_su_co_khong_ton_tai_tra_404()
    {
        var responder = await _fx.ResponderAsync();

        var response = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{Guid.NewGuid()}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- TC-BIZ-05 · US-BIZ-05/AC-01 ----------

    [Fact]
    public async Task TCBIZ05_di_tron_ba_trang_thai_sinh_dung_hai_dong_lich_su()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var me = await responder.GetFromJsonAsync<MeDto>("/api/auth/me", ApiFactory.Json);

        var incident = await CreateIncidentAsync(support, "Đi trọn vòng đời");
        await TransitionAsync(responder, incident.Id, "Mitigating");
        await TransitionAsync(responder, incident.Id, "Resolved");

        var history = await GetHistoryAsync(responder, incident.Id);

        Assert.Equal(2, history.Count);
        Assert.Equal(("Investigating", "Mitigating"), (history[0].FromStatus, history[0].ToStatus));
        Assert.Equal(("Mitigating", "Resolved"), (history[1].FromStatus, history[1].ToStatus));
        Assert.True(history[0].ChangedAt <= history[1].ChangedAt);
        Assert.All(history, h => Assert.Equal(me!.Id, h.ChangedBy.Id));
    }

    // ---------- TC-NFR-02 · NFR-REL-01 ----------

    /// <summary>
    /// Fault injection ngay sau khi ghi lịch sử: toàn bộ transaction phải rollback,
    /// status giữ nguyên và không có history row mồ côi.
    /// </summary>
    [Fact]
    public async Task TCNFR02_loi_giua_transaction_thi_rollback_ca_hai_thao_tac()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var incident = await CreateIncidentAsync(support, "Fault injection rollback");

        _fx.Factory.FaultHook.ShouldThrow = true;
        try
        {
            var response = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
                new { targetStatus = "Mitigating" }, ApiFactory.Json);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
            Assert.True(problem.TryGetProperty("correlationId", out _));
        }
        finally
        {
            _fx.Factory.FaultHook.ShouldThrow = false;
        }

        await using var db = _fx.Factory.CreateDbContext();
        var stored = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == incident.Id);
        Assert.Equal(IncidentStatus.Investigating, stored.Status);
        Assert.Null(stored.MitigatingAt);
        Assert.Equal(0, await db.IncidentStatusHistory.CountAsync(h => h.IncidentId == incident.Id));
    }

    // ---------- TC-BIZ-09 ----------

    [Fact]
    public async Task TCBIZ09_loc_theo_status_tra_dung_tap_con()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();

        var open = await CreateIncidentAsync(support, "Lọc: đang điều tra");
        var mitigating = await CreateIncidentAsync(support, "Lọc: đang khắc phục");
        await TransitionAsync(responder, mitigating.Id, "Mitigating");

        var page = await responder.GetFromJsonAsync<PagedDto<IncidentDto>>(
            "/api/projects/support/incidents?status=Mitigating&page=1&pageSize=100", ApiFactory.Json);

        Assert.All(page!.Items, i => Assert.Equal("Mitigating", i.Status));
        Assert.Contains(page.Items, i => i.Id == mitigating.Id);
        Assert.DoesNotContain(page.Items, i => i.Id == open.Id);
        Assert.True(page.TotalCount >= 1);
    }

    [Fact]
    public async Task TCBIZ09_pageSize_vuot_100_tra_400()
    {
        var responder = await _fx.ResponderAsync();
        var response = await responder.GetAsync("/api/projects/support/incidents?page=1&pageSize=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Loc_theo_khoang_thoi_gian_tao()
    {
        var support = await _fx.SupportAsync();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var incident = await CreateIncidentAsync(support, "Lọc theo thời gian tạo");

        var inRange = await support.GetFromJsonAsync<PagedDto<IncidentDto>>(
            $"/api/projects/support/incidents?createdFrom={Uri.EscapeDataString(before.ToString("O"))}&pageSize=100",
            ApiFactory.Json);
        Assert.Contains(inRange!.Items, i => i.Id == incident.Id);

        var outOfRange = await support.GetFromJsonAsync<PagedDto<IncidentDto>>(
            $"/api/projects/support/incidents?createdTo={Uri.EscapeDataString(before.ToString("O"))}&pageSize=100",
            ApiFactory.Json);
        Assert.DoesNotContain(outOfRange!.Items, i => i.Id == incident.Id);
    }

    // ---------- Helper dùng chung ----------

    internal static async Task<IncidentDto> CreateIncidentAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/projects/support/incidents",
            new { title, description = "Sinh bởi integration test", severity = "Medium" }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json))!;
    }

    internal static async Task<IncidentDto> TransitionAsync(HttpClient client, Guid id, string target)
    {
        var response = await client.PatchAsJsonAsync($"/api/projects/support/incidents/{id}/status",
            new { targetStatus = target }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json))!;
    }

    internal static async Task<List<HistoryDto>> GetHistoryAsync(HttpClient client, Guid id)
        => (await client.GetFromJsonAsync<List<HistoryDto>>(
            $"/api/projects/support/incidents/{id}/history", ApiFactory.Json))!;

    private async Task<int> CountIncidentsAsync()
    {
        await using var db = _fx.Factory.CreateDbContext();
        return await db.Incidents.IgnoreQueryFilters().CountAsync();
    }
}

public sealed record UserRefDto(Guid Id, string DisplayName, string Email);

public sealed record IncidentDto(
    Guid Id, string Title, string? Description, string Severity, string Status,
    string? AllowedNextStatus, UserRefDto Reporter, UserRefDto? Assignee,
    DateTimeOffset CreatedAt, DateTimeOffset? MitigatingAt, DateTimeOffset? ResolvedAt,
    UserRefDto? Resolver, bool IsDeleted);

public sealed record HistoryDto(
    Guid Id, string FromStatus, string ToStatus, UserRefDto ChangedBy,
    DateTimeOffset ChangedAt, string? Note);

public sealed record PagedDto<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public sealed record MeDto(Guid Id, string Email, string DisplayName, string[] Roles, string[] Permissions);
