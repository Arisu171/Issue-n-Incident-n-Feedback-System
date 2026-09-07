using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// UC-BIZ-09 · FR-BIZ-14 — kênh hội thoại hai chiều: bình luận trên sự cố, trả lời phản hồi
/// và vòng đời tiếp nhận của feedback (New → Acknowledged → Responded), kèm lời xác nhận
/// tự động khi ghi nhận phản hồi.
///
/// Ranh giới cần chứng minh: hội thoại đi theo đúng ranh giới đọc của bản ghi cha — khách
/// hàng nói được trên sự cố của mình nhưng không nói được trên sự cố của người khác, và
/// đọc được câu trả lời trên phản hồi của mình nhưng không trả lời nhân danh doanh nghiệp.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ConversationTests
{
    private readonly ApiFixture _fx;

    public ConversationTests(ApiFixture fx) => _fx = fx;

    // ---------------- Bình luận trên sự cố ----------------

    [Fact]
    public async Task Khach_hang_va_responder_trao_doi_duoc_tren_su_co()
    {
        var customer = await _fx.CustomerAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(customer, "Trao đổi hai chiều trên sự cố");

        var first = await customer.PostAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/comments",
            new { body = "Lỗi xảy ra khi tôi bấm thanh toán." }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await responder.PostAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/comments",
            new { body = "Bạn cho mình xin mã lỗi hiển thị trên màn hình nhé?" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // Cả hai phía cùng đọc được một luồng hội thoại, thứ tự thời gian tăng dần.
        var thread = await customer.GetFromJsonAsync<List<CommentDto>>(
            $"/api/projects/support/incidents/{incident.Id}/comments", ApiFactory.Json);

        Assert.Equal(2, thread!.Count);
        Assert.Equal("Lỗi xảy ra khi tôi bấm thanh toán.", thread[0].Body);
        Assert.True(thread[0].CreatedAt <= thread[1].CreatedAt);
    }

    /// <summary>BOLA trên hội thoại: cửa bình luận phải hẹp đúng bằng cửa đọc sự cố.</summary>
    [Fact]
    public async Task Khach_hang_doc_va_viet_duoc_hoi_thoai_trong_project_minh()
    {
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();

        var incident = await CreateIncidentAsync(alice, "Hội thoại của khách hàng A");

        // Đợt 6b: khách hàng xem được mọi sự cố trong project mình với tới, nên hội thoại cũng
        // mở theo. Bình luận vốn chưa bao giờ là dữ liệu nội bộ — thứ nội bộ là ghi chú chuyển
        // trạng thái, và nó khoá bằng `incident.note.read` riêng.
        Assert.Equal(HttpStatusCode.OK,
            (await bob.GetAsync($"/api/projects/support/incidents/{incident.Id}/comments")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await bob.PostAsJsonAsync(
            $"/api/projects/support/incidents/{incident.Id}/comments",
            new { body = "Tôi cũng gặp lỗi này." }, ApiFactory.Json)).StatusCode);
    }

    /// <summary>
    /// Tài khoản chỉ đọc: có <c>incident.read.all</c> nên xem được mọi sự cố, nhưng không có
    /// <c>incident.comment</c> nên không viết được. Không dùng `customer` thay được — customer
    /// **có** <c>incident.comment</c>, test sẽ xanh vì lý do khác hẳn.
    /// </summary>
    [Fact]
    public async Task Tai_khoan_chi_doc_xem_duoc_hoi_thoai_nhung_khong_viet_duoc()
    {
        var customer = await _fx.CustomerAsync();
        var readOnly = await _fx.ReadOnlyAsync();

        var incident = await CreateIncidentAsync(customer, "Tài khoản chỉ đọc đứng xem hội thoại");

        Assert.Equal(HttpStatusCode.OK,
            (await readOnly.GetAsync($"/api/projects/support/incidents/{incident.Id}/comments")).StatusCode);

        var denied = await readOnly.PostAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/comments",
            new { body = "Tài khoản chỉ đọc cố viết bình luận." }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var problem = await denied.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("incident.comment", problem.GetProperty("requiredPermission").GetString());
    }

    /// <summary>Lời giải thích sau khi đóng là hội thoại bình thường — Resolved không đóng miệng ai.</summary>
    [Fact]
    public async Task Su_co_da_dong_van_nhan_binh_luan()
    {
        var customer = await _fx.CustomerAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(customer, "Bình luận sau khi sự cố đã đóng");
        foreach (var status in new[] { "Mitigating", "Resolved" })
        {
            var moved = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
                new { targetStatus = status }, ApiFactory.Json);
            moved.EnsureSuccessStatusCode();
        }

        var response = await responder.PostAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/comments",
            new { body = "Nguyên nhân gốc: hết dung lượng đĩa. Đã bổ sung cảnh báo." }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Binh_luan_rong_bi_tu_choi_400()
    {
        var customer = await _fx.CustomerAsync();
        var incident = await CreateIncidentAsync(customer, "Bình luận rỗng không được nhận");

        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync(
            $"/api/projects/support/incidents/{incident.Id}/comments", new { body = "" }, ApiFactory.Json)).StatusCode);
    }

    // ---------------- Vòng đời feedback + phản hồi tự động ----------------

    /// <summary>
    /// FR-BIZ-14 — auto-acknowledge: ngay khi phản hồi được ghi nhận, luồng trả lời đã có lời
    /// xác nhận tự động (không người ký), còn trạng thái vẫn là <c>New</c> vì chưa ai thật sự
    /// trả lời.
    /// </summary>
    [Fact]
    public async Task Phan_hoi_moi_nhan_duoc_loi_xac_nhan_tu_dong()
    {
        var customer = await _fx.CustomerAsync();

        var feedback = await CreateFeedbackAsync(customer, "Ứng dụng chậm vào giờ cao điểm.");
        Assert.Equal("New", feedback.Status);

        var replies = await customer.GetFromJsonAsync<List<ReplyDto>>(
            $"/api/projects/support/feedbacks/{feedback.Id}/replies", ApiFactory.Json);

        var ack = Assert.Single(replies!);
        Assert.True(ack.IsAutomatic);
        Assert.Null(ack.Responder);
        Assert.False(string.IsNullOrWhiteSpace(ack.Body));
    }

    /// <summary>
    /// Vòng khép kín khách hàng ↔ doanh nghiệp: Support trả lời → trạng thái thành
    /// <c>Responded</c> → khách hàng đọc được câu trả lời trên chính phản hồi của mình.
    /// </summary>
    [Fact]
    public async Task Support_tra_loi_va_khach_hang_doc_duoc_cau_tra_loi()
    {
        var customer = await _fx.CustomerAsync();
        var support = await _fx.SupportAsync();

        var feedback = await CreateFeedbackAsync(customer, "Tôi muốn được hỗ trợ xuất hóa đơn.");

        var replied = await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/replies",
            new { body = "Chào bạn, bộ phận kế toán sẽ liên hệ trong hôm nay." }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, replied.StatusCode);

        var after = await customer.GetFromJsonAsync<FeedbackDto>(
            $"/api/projects/support/feedbacks/{feedback.Id}", ApiFactory.Json);
        Assert.Equal("Responded", after!.Status);

        var replies = await customer.GetFromJsonAsync<List<ReplyDto>>(
            $"/api/projects/support/feedbacks/{feedback.Id}/replies", ApiFactory.Json);

        // Auto-ack đứng trước, câu trả lời của con người đứng sau và có người ký tên.
        Assert.Equal(2, replies!.Count);
        Assert.True(replies[0].IsAutomatic);
        Assert.False(replies[1].IsAutomatic);
        Assert.NotNull(replies[1].Responder);
    }

    /// <summary>Khách hàng không có <c>feedback.respond</c> — trả lời là tiếng nói của doanh nghiệp.</summary>
    [Fact]
    public async Task Khach_hang_khong_tra_loi_duoc_nhan_danh_doanh_nghiep()
    {
        var customer = await _fx.CustomerAsync();
        var feedback = await CreateFeedbackAsync(customer, "Phản hồi của chính khách hàng.");

        var denied = await customer.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/replies",
            new { body = "Tự trả lời phản hồi của mình." }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var problem = await denied.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("feedback.respond", problem.GetProperty("requiredPermission").GetString());
    }

    /// <summary>BR-BIZ-11 áp lên cả luồng trả lời: không đọc được câu trả lời của người khác.</summary>
    [Fact]
    public async Task Khach_hang_khong_doc_duoc_luong_tra_loi_cua_nguoi_khac()
    {
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();

        var feedback = await CreateFeedbackAsync(alice, "Nội dung riêng tư trong luồng trả lời.");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await bob.GetAsync($"/api/projects/support/feedbacks/{feedback.Id}/replies")).StatusCode);
    }

    /// <summary>BR-BIZ-12 — gắn vào sự cố nghĩa là đã tiếp nhận: New → Acknowledged.</summary>
    [Fact]
    public async Task Gan_feedback_vao_su_co_chuyen_trang_thai_sang_Acknowledged()
    {
        var support = await _fx.SupportAsync();

        var incident = await CreateIncidentAsync(support, "Sự cố nhận feedback để phân loại");
        var feedback = await CreateFeedbackAsync(support, "Phản hồi chờ phân loại vào sự cố.");
        Assert.Equal("New", feedback.Status);

        var linked = await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/link",
            new { incidentId = incident.Id }, ApiFactory.Json);
        linked.EnsureSuccessStatusCode();

        var after = await linked.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json);
        Assert.Equal("Acknowledged", after!.Status);
    }

    /// <summary>Responded là mốc cao hơn Acknowledged — phân loại muộn không được hạ cấp trạng thái.</summary>
    [Fact]
    public async Task Gan_vao_su_co_sau_khi_da_tra_loi_khong_ha_cap_trang_thai()
    {
        var support = await _fx.SupportAsync();

        var incident = await CreateIncidentAsync(support, "Phân loại sau khi đã trả lời");
        var feedback = await CreateFeedbackAsync(support, "Được trả lời trước, phân loại sau.");

        (await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/replies",
            new { body = "Đã ghi nhận, cảm ơn bạn." }, ApiFactory.Json)).EnsureSuccessStatusCode();

        var linked = await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/link",
            new { incidentId = incident.Id }, ApiFactory.Json);
        linked.EnsureSuccessStatusCode();

        var after = await linked.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json);
        Assert.Equal("Responded", after!.Status);
    }

    // ---------------- ShouldClaim: admin không bị tự gán việc ----------------

    /// <summary>
    /// BR-BIZ-10 — người có <c>incident.manage_any</c> thao tác nhân danh vận hành, không phải
    /// nhận việc: admin chuyển trạng thái sự cố chưa ai nhận thì sự cố vẫn phải chưa ai nhận.
    /// </summary>
    [Fact]
    public async Task Admin_chuyen_trang_thai_khong_bi_tu_gan_lam_nguoi_phu_trach()
    {
        var support = await _fx.SupportAsync();
        var admin = await _fx.AdminAsync();

        var incident = await CreateIncidentAsync(support, "Admin thao tác không nhận việc thay ai");
        Assert.Null(incident.Assignee);

        var moved = await admin.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);
        moved.EnsureSuccessStatusCode();

        var after = await moved.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json);
        Assert.Null(after!.Assignee);
    }

    // ---------------- Helpers ----------------

    private static async Task<IncidentDto> CreateIncidentAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/projects/support/incidents",
            new { title, severity = "Medium" }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json))!;
    }

    private static async Task<FeedbackDto> CreateFeedbackAsync(HttpClient client, string content)
    {
        var response = await client.PostAsJsonAsync("/api/projects/support/feedbacks",
            new { channel = "Web", content }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json))!;
    }

    private sealed record UserRefDto(Guid Id, string DisplayName, string Email);

    private sealed record IncidentDto(Guid Id, string Title, string Status, UserRefDto? Assignee);

    private sealed record FeedbackDto(Guid Id, string Status, Guid? IncidentId);

    private sealed record CommentDto(
        Guid Id, Guid IncidentId, UserRefDto Author, string Body, DateTimeOffset CreatedAt);

    private sealed record ReplyDto(
        Guid Id, Guid FeedbackId, UserRefDto? Responder, bool IsAutomatic, string Body, DateTimeOffset CreatedAt);
}
