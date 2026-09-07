using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static IncidentTracker.Api.Tests.Integration.IncidentLifecycleTests;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>TC-BIZ-04, TC-BIZ-06, TC-BIZ-07 — gán người xử lý, xóa mềm và phản hồi khách hàng.</summary>
[Collection(ApiCollection.Name)]
public class AssignDeleteFeedbackTests
{
    private readonly ApiFixture _fx;

    public AssignDeleteFeedbackTests(ApiFixture fx) => _fx = fx;

    // ---------- TC-BIZ-07 · gán người xử lý ----------

    [Fact]
    public async Task TCBIZ07_gan_cho_user_hop_le_tra_204_va_idempotent()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var responderMe = await responder.GetFromJsonAsync<MeDto>("/api/auth/me", ApiFactory.Json);

        var incident = await CreateIncidentAsync(support, "Gán người xử lý hợp lệ");

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsync($"/api/projects/support/incidents/{incident.Id}/assignee/{responderMe!.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsync($"/api/projects/support/incidents/{incident.Id}/assignee/{responderMe.Id}", null)).StatusCode);

        var after = await admin.GetFromJsonAsync<IncidentDto>($"/api/projects/support/incidents/{incident.Id}", ApiFactory.Json);
        Assert.Equal(responderMe.Id, after!.Assignee!.Id);
    }

    /// <summary>BR-BIZ-08 — assignee thiếu <c>incident.update_status</c> thì trả 409.</summary>
    [Fact]
    public async Task TCBIZ07_assignee_thieu_quyen_tra_409()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();
        var readOnly = await _fx.ReadOnlyAsync();
        var readOnlyMe = await readOnly.GetFromJsonAsync<MeDto>("/api/auth/me", ApiFactory.Json);

        var incident = await CreateIncidentAsync(support, "Gán cho người thiếu quyền");

        var response = await admin.PutAsync($"/api/projects/support/incidents/{incident.Id}/assignee/{readOnlyMe!.Id}", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    /// <summary>BR-BIZ-08 — assignee inactive thì trả 409.</summary>
    [Fact]
    public async Task TCBIZ07_assignee_inactive_tra_409()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();

        var created = await admin.PostAsJsonAsync("/api/users",
            new
            {
                email = $"inactive-{Guid.NewGuid():N}@test.local",
                displayName = "Đã vô hiệu hóa",
                password = "Secret#12345",
                roles = new[] { "responder" }
            }, ApiFactory.Json);
        created.EnsureSuccessStatusCode();
        var user = await created.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        var userId = user.GetProperty("id").GetGuid();

        (await admin.PatchAsJsonAsync($"/api/users/{userId}", new { isActive = false }, ApiFactory.Json))
            .EnsureSuccessStatusCode();

        var incident = await CreateIncidentAsync(support, "Gán cho người đã vô hiệu hóa");

        var response = await admin.PutAsync($"/api/projects/support/incidents/{incident.Id}/assignee/{userId}", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Support_khong_co_quyen_assign_tra_403()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var responderMe = await responder.GetFromJsonAsync<MeDto>("/api/auth/me", ApiFactory.Json);
        var incident = await CreateIncidentAsync(support, "Support thử gán người xử lý");

        var response = await support.PutAsync($"/api/projects/support/incidents/{incident.Id}/assignee/{responderMe!.Id}", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- TC-BIZ-04 · xóa mềm ----------

    /// <summary>US-BIZ-07/AC-02 — sự cố chưa đóng không xóa được.</summary>
    [Fact]
    public async Task TCBIZ04_xoa_su_co_chua_dong_tra_409()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();
        var incident = await CreateIncidentAsync(support, "Xóa sự cố chưa đóng");

        var response = await admin.DeleteAsync($"/api/projects/support/incidents/{incident.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = _fx.Factory.CreateDbContext();
        Assert.False((await db.Incidents.IgnoreQueryFilters().SingleAsync(i => i.Id == incident.Id)).IsDeleted);
    }

    /// <summary>US-BIZ-07/AC-01 — xóa mềm sự cố đã đóng; lịch sử vẫn còn nguyên trong DB.</summary>
    [Fact]
    public async Task TCBIZ04_xoa_mem_su_co_da_dong_va_giu_lich_su()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(support, "Xóa mềm sự cố đã đóng");
        await TransitionAsync(responder, incident.Id, "Mitigating");
        await TransitionAsync(responder, incident.Id, "Resolved");

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/projects/support/incidents/{incident.Id}")).StatusCode);

        // Biến mất khỏi danh sách và khỏi endpoint chi tiết.
        var page = await admin.GetFromJsonAsync<PagedDto<IncidentDto>>(
            "/api/projects/support/incidents?pageSize=100", ApiFactory.Json);
        Assert.DoesNotContain(page!.Items, i => i.Id == incident.Id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/projects/support/incidents/{incident.Id}")).StatusCode);

        // Nhưng lịch sử vẫn tra cứu được — bằng chứng đo SLA (GOAL-BIZ-02, ADR-003).
        var history = await GetHistoryAsync(admin, incident.Id);
        Assert.Equal(2, history.Count);

        await using var db = _fx.Factory.CreateDbContext();
        Assert.True((await db.Incidents.IgnoreQueryFilters().SingleAsync(i => i.Id == incident.Id)).IsDeleted);
        Assert.Equal(2, await db.IncidentStatusHistory.CountAsync(h => h.IncidentId == incident.Id));
    }

    /// <summary>
    /// Thuật ngữ #27 của README: gọi DELETE lần hai phải giữ nguyên trạng thái thay vì báo lỗi.
    /// Nếu không bỏ qua global query filter thì lần hai sẽ ra 404.
    /// </summary>
    [Fact]
    public async Task TCBIZ04_xoa_mem_lan_hai_van_tra_204()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(support, "Xóa mềm hai lần");
        await TransitionAsync(responder, incident.Id, "Mitigating");
        await TransitionAsync(responder, incident.Id, "Resolved");

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/projects/support/incidents/{incident.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.DeleteAsync($"/api/projects/support/incidents/{incident.Id}")).StatusCode);
    }

    [Fact]
    public async Task Xoa_su_co_khong_ton_tai_tra_404()
    {
        var admin = await _fx.AdminAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.DeleteAsync($"/api/projects/support/incidents/{Guid.NewGuid()}")).StatusCode);
    }

    // ---------- TC-BIZ-06 · Feedback ----------

    [Fact]
    public async Task TCBIZ06_tao_feedback_chua_phan_loai_va_gan_vao_su_co_mo()
    {
        var support = await _fx.SupportAsync();
        var incident = await CreateIncidentAsync(support, "Sự cố nhận phản hồi");

        var created = await support.PostAsJsonAsync("/api/projects/support/feedbacks",
            new
            {
                channel = "Hotline",
                customerEmail = "khach@example.com",
                content = "Tôi không thanh toán được từ sáng nay."
            }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var feedback = await created.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json);
        Assert.Null(feedback!.IncidentId);

        var linked = await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/link",
            new { incidentId = incident.Id }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var afterLink = await linked.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json);
        Assert.Equal(incident.Id, afterLink!.IncidentId);

        // Idempotent với cùng incidentId.
        Assert.Equal(HttpStatusCode.OK,
            (await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/link",
                new { incidentId = incident.Id }, ApiFactory.Json)).StatusCode);
    }

    /// <summary>
    /// Gắn nhầm thì sửa được: gắn lại vào một sự cố khác đổi liên kết, không phải lỗi.
    ///
    /// Giao diện bảng phản hồi nay bày ra ô "đổi sự cố" cho hàng đã gắn, nên hành vi này thành
    /// một lời hứa với người dùng chứ không còn là chuyện tình cờ đúng — test giữ nó lại.
    /// </summary>
    [Fact]
    public async Task Gan_lai_vao_su_co_khac_thi_doi_lien_ket()
    {
        var support = await _fx.SupportAsync();
        var first = await CreateIncidentAsync(support, "Sự cố gắn nhầm");
        var second = await CreateIncidentAsync(support, "Sự cố đúng");

        var created = await support.PostAsJsonAsync("/api/projects/support/feedbacks",
            new { channel = "Email", content = "Phản hồi bị phân loại nhầm sự cố." }, ApiFactory.Json);
        var feedback = await created.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json);

        (await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback!.Id}/link",
            new { incidentId = first.Id }, ApiFactory.Json)).EnsureSuccessStatusCode();

        var moved = await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/link",
            new { incidentId = second.Id }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var after = await moved.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json);
        Assert.Equal(second.Id, after!.IncidentId);
        Assert.NotEqual(first.Id, after.IncidentId);
    }

    /// <summary>US-BIZ-04/AC-02 — gắn vào sự cố đã đóng trả 409 và feedback giữ nguyên.</summary>
    [Fact]
    public async Task TCBIZ06_gan_vao_su_co_da_dong_tra_409()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(support, "Sự cố đã đóng không nhận phản hồi");
        await TransitionAsync(responder, incident.Id, "Mitigating");
        await TransitionAsync(responder, incident.Id, "Resolved");

        var created = await support.PostAsJsonAsync("/api/projects/support/feedbacks",
            new { channel = "Email", content = "Phản hồi tới muộn sau khi sự cố đã đóng." },
            ApiFactory.Json);
        created.EnsureSuccessStatusCode();
        var feedback = await created.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json);

        var response = await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback!.Id}/link",
            new { incidentId = incident.Id }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var after = await support.GetFromJsonAsync<FeedbackDto>(
            $"/api/projects/support/feedbacks/{feedback.Id}", ApiFactory.Json);
        Assert.Null(after!.IncidentId);
    }

    [Fact]
    public async Task Hang_doi_feedback_chua_phan_loai_loc_dung()
    {
        var support = await _fx.SupportAsync();
        var incident = await CreateIncidentAsync(support, "Sự cố cho hàng đợi feedback");

        var unlinked = await CreateFeedbackAsync(support, null);
        var linked = await CreateFeedbackAsync(support, incident.Id);

        var queue = await support.GetFromJsonAsync<PagedDto<FeedbackDto>>(
            "/api/projects/support/feedbacks?unlinkedOnly=true&pageSize=100", ApiFactory.Json);

        Assert.All(queue!.Items, f => Assert.Null(f.IncidentId));
        Assert.Contains(queue.Items, f => f.Id == unlinked.Id);
        Assert.DoesNotContain(queue.Items, f => f.Id == linked.Id);
    }

    [Fact]
    public async Task Noi_dung_phan_hoi_qua_ngan_tra_400()
    {
        var support = await _fx.SupportAsync();

        var response = await support.PostAsJsonAsync("/api/projects/support/feedbacks",
            new { channel = "Web", content = "ngắn" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>PII — responder có <c>feedback.read</c> nhưng không có <c>feedback.create</c>.</summary>
    [Fact]
    public async Task Nguoi_khong_co_quyen_doc_feedback_tra_403()
    {
        var readOnly = await _fx.ReadOnlyAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.GetAsync("/api/projects/support/feedbacks")).StatusCode);
    }

    private static async Task<FeedbackDto> CreateFeedbackAsync(HttpClient client, Guid? incidentId)
    {
        var response = await client.PostAsJsonAsync("/api/projects/support/feedbacks",
            new
            {
                channel = "Web",
                customerEmail = "khach@example.com",
                content = "Nội dung phản hồi đủ dài cho ràng buộc kiểm tra.",
                incidentId
            }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json))!;
    }
}

public sealed record FeedbackDto(
    Guid Id, string Channel, string? CustomerEmail, string Content,
    Guid? IncidentId, string? IncidentTitle, string? IncidentStatus,
    Guid CreatedBy, string CreatedByName, DateTimeOffset CreatedAt);
