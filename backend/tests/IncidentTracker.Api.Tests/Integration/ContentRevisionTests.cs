using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Sửa nội dung mà vẫn giữ được bằng chứng: sự cố, bình luận sự cố, phản hồi khách hàng và
/// câu trả lời đều sửa được, và **mọi** lần sửa để lại một dòng lịch sử ký tên người bấm nút.
///
/// Ba điều cần chứng minh, vì thiếu bất kỳ điều nào thì tính năng này là một đường xóa dấu vết
/// chứ không phải một đường sửa:
///   1. Ai được sửa — tác giả, hoặc người có quyền cấp cao của module. Không có cửa nào khác.
///   2. Sửa xong thì giá trị cũ vẫn đọc lại được nguyên văn, kèm tên người đã thay nó.
///   3. Lịch sử chịu đúng ranh giới đọc của bản ghi cha — không có đường vòng qua
///      <c>/revisions</c> để đọc thứ mình không được đọc.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ContentRevisionTests
{
    private readonly ApiFixture _fx;

    public ContentRevisionTests(ApiFixture fx) => _fx = fx;

    // ---------------- Sự cố ----------------

    /// <summary>
    /// Người báo cáo sửa bài của chính mình: đổi ba trường thì có đúng ba dòng lịch sử, mỗi
    /// dòng giữ nguyên văn giá trị cũ, và cả ba đều ký tên chính họ — chữ ký không phải hình
    /// phạt dành cho người kiểm duyệt, nó là dấu vết của mọi thay đổi.
    /// </summary>
    [Fact]
    public async Task Nguoi_bao_cao_sua_duoc_su_co_cua_minh_va_lich_su_giu_nguyen_van_cu()
    {
        var customer = await _fx.CustomerAsync();
        var incident = await CreateIncidentAsync(customer, "Tiêu đề ban đầu của sự cố");

        var updated = await PatchAsync<IncidentDto>(customer,
            $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Tiêu đề đã sửa lại", description = "Mô tả bổ sung", severity = "High" });

        Assert.Equal("Tiêu đề đã sửa lại", updated.Title);
        Assert.Equal("High", updated.Severity);
        Assert.NotNull(updated.LastEdit);

        var revisions = await customer.GetFromJsonAsync<List<RevisionDto>>(
            $"/api/projects/support/incidents/{incident.Id}/revisions", ApiFactory.Json);

        Assert.Equal(3, revisions!.Count);

        var title = revisions.Single(r => r.Field == "title");
        Assert.Equal("Tiêu đề ban đầu của sự cố", title.OldValue);
        Assert.Equal("Tiêu đề đã sửa lại", title.NewValue);

        // Tác giả tự sửa bài mình: có chữ ký, nhưng không phải "sửa hộ".
        Assert.All(revisions, r => Assert.False(r.OnBehalf));
        Assert.All(revisions, r => Assert.Equal(updated.Reporter.Id, r.EditedBy.Id));

        Assert.Equal("Medium", revisions.Single(r => r.Field == "severity").OldValue);
        Assert.Null(revisions.Single(r => r.Field == "description").OldValue);
    }

    /// <summary>
    /// Admin sửa bài người khác: vẫn được, nhưng dòng lịch sử mang <c>onBehalf</c> và ký tên
    /// admin — đọc lịch sử là phân biệt được ngay "khách nói lại" với "người khác viết lại".
    /// </summary>
    [Fact]
    public async Task Admin_sua_duoc_su_co_cua_nguoi_khac_va_de_lai_chu_ky_cua_minh()
    {
        var customer = await _fx.CustomerAsync();
        var admin = await _fx.AdminAsync();

        var incident = await CreateIncidentAsync(customer, "Sự cố do khách hàng báo cáo");

        var updated = await PatchAsync<IncidentDto>(admin,
            $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Tiêu đề do quản trị viên chuẩn hóa", reason = "Chuẩn hóa cách đặt tên sự cố" });

        // Người báo cáo không đổi — sửa hộ không phải là chiếm quyền tác giả.
        Assert.Equal(incident.Reporter.Id, updated.Reporter.Id);

        var revision = Assert.Single((await customer.GetFromJsonAsync<List<RevisionDto>>(
            $"/api/projects/support/incidents/{incident.Id}/revisions", ApiFactory.Json))!);

        Assert.True(revision.OnBehalf);
        Assert.NotEqual(incident.Reporter.Id, revision.EditedBy.Id);
        Assert.Equal("Chuẩn hóa cách đặt tên sự cố", revision.Reason);
        Assert.Equal(revision.EditedBy.Id, updated.LastEdit!.By.Id);
    }

    /// <summary>
    /// Nhận việc là nhận trách nhiệm xử lý, không phải quyền viết lại lời tường thuật của
    /// người báo. Responder không có <c>incident.manage_any</c> nên dừng ở 403.
    /// </summary>
    [Fact]
    public async Task Nguoi_xu_ly_khong_sua_duoc_noi_dung_su_co_cua_nguoi_khac()
    {
        var customer = await _fx.CustomerAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(customer, "Sự cố mà người xử lý muốn viết lại");

        var denied = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}",
            new { title = "Người xử lý tự đổi tiêu đề" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var problem = await denied.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("incident.manage_any", problem.GetProperty("requiredPermission").GetString());
    }

    /// <summary>
    /// Bấm "Lưu" mà không đổi gì không phải là một lần sửa: không dòng lịch sử, không chữ ký.
    /// Nếu không, lịch sử sẽ đầy những dòng rỗng và những dòng có thật chìm mất trong đó.
    /// </summary>
    [Fact]
    public async Task Gui_dung_gia_tri_cu_thi_khong_sinh_dong_lich_su_nao()
    {
        var customer = await _fx.CustomerAsync();
        var incident = await CreateIncidentAsync(customer, "Sự cố không có gì để sửa");

        var updated = await PatchAsync<IncidentDto>(customer,
            $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Sự cố không có gì để sửa", severity = "Medium" });

        Assert.Null(updated.LastEdit);
        Assert.Empty((await customer.GetFromJsonAsync<List<RevisionDto>>(
            $"/api/projects/support/incidents/{incident.Id}/revisions", ApiFactory.Json))!);
    }

    /// <summary>
    /// Đường sửa nội dung KHÔNG được trở thành đường thứ hai để đổi trạng thái: vòng đời một
    /// chiều và lịch sử trạng thái của nó là thứ cả hệ thống dựng lên để bảo vệ (FR-BIZ-05).
    /// Trường lạ trong body bị bỏ qua khi bind, nên trạng thái phải y nguyên.
    /// </summary>
    [Fact]
    public async Task Sua_noi_dung_khong_doi_duoc_trang_thai_su_co()
    {
        var customer = await _fx.CustomerAsync();
        var incident = await CreateIncidentAsync(customer, "Sự cố bị thử đổi trạng thái qua cửa sau");

        var updated = await PatchAsync<IncidentDto>(customer,
            $"/api/projects/support/incidents/{incident.Id}",
            new { title = "Đổi tiêu đề kèm một trường status lạ", status = "Resolved" });

        Assert.Equal("Investigating", updated.Status);
    }

    // ---------------- Bình luận trên sự cố ----------------

    [Fact]
    public async Task Tac_gia_sua_duoc_binh_luan_va_loi_cu_van_doc_lai_duoc()
    {
        var customer = await _fx.CustomerAsync();
        var incident = await CreateIncidentAsync(customer, "Sự cố có hội thoại cần sửa");

        var created = await customer.PostAsJsonAsync(
            $"/api/projects/support/incidents/{incident.Id}/comments",
            new { body = "Lỗi xảy ra lúc 9 giờ sáng." }, ApiFactory.Json);
        created.EnsureSuccessStatusCode();
        var comment = (await created.Content.ReadFromJsonAsync<CommentDto>(ApiFactory.Json))!;

        var updated = await PatchAsync<CommentDto>(customer,
            $"/api/projects/support/incidents/{incident.Id}/comments/{comment.Id}",
            new { body = "Đính chính: lỗi xảy ra lúc 10 giờ sáng." });

        Assert.Equal("Đính chính: lỗi xảy ra lúc 10 giờ sáng.", updated.Body);
        Assert.NotNull(updated.LastEdit);

        var revision = Assert.Single((await customer.GetFromJsonAsync<List<RevisionDto>>(
            $"/api/projects/support/incidents/{incident.Id}/comments/{comment.Id}/revisions", ApiFactory.Json))!);

        Assert.Equal("body", revision.Field);
        Assert.Equal("Lỗi xảy ra lúc 9 giờ sáng.", revision.OldValue);
        Assert.False(revision.OnBehalf);
    }

    /// <summary>
    /// Khách hàng khác đọc được hội thoại (cùng project) nhưng không sửa được lời người khác —
    /// đọc được và viết lại được là hai chuyện hoàn toàn khác nhau.
    /// </summary>
    [Fact]
    public async Task Khach_hang_khac_khong_sua_duoc_binh_luan_cua_nguoi_khac()
    {
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();

        var incident = await CreateIncidentAsync(alice, "Sự cố có bình luận của người khác");
        var created = await alice.PostAsJsonAsync(
            $"/api/projects/support/incidents/{incident.Id}/comments",
            new { body = "Bình luận của Alice." }, ApiFactory.Json);
        created.EnsureSuccessStatusCode();
        var comment = (await created.Content.ReadFromJsonAsync<CommentDto>(ApiFactory.Json))!;

        var denied = await bob.PatchAsJsonAsync(
            $"/api/projects/support/incidents/{incident.Id}/comments/{comment.Id}",
            new { body = "Bob viết lại lời của Alice." }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    // ---------------- Phản hồi khách hàng ----------------

    /// <summary>
    /// Support sửa lời khách hàng: được, vì đó là người đang đứng ra tiếp khách — nhưng nguyên
    /// văn cũ ở lại trong lịch sử kèm tên họ.
    /// </summary>
    [Fact]
    public async Task Support_sua_duoc_phan_hoi_cua_khach_va_nguyen_van_cu_o_lai_lich_su()
    {
        var customer = await _fx.CustomerAsync();
        var support = await _fx.SupportAsync();

        var feedback = await CreateFeedbackAsync(customer, "Ứng dụng báo lỗi khi tôi thanh toán.");

        var updated = await PatchAsync<FeedbackDto>(support,
            $"/api/projects/support/feedbacks/{feedback.Id}",
            new { content = "Ứng dụng báo lỗi 502 khi khách hàng thanh toán bằng thẻ nội địa.", reason = "Bổ sung mã lỗi" });

        Assert.NotNull(updated.LastEdit);

        var revision = Assert.Single((await support.GetFromJsonAsync<List<RevisionDto>>(
            $"/api/projects/support/feedbacks/{feedback.Id}/revisions", ApiFactory.Json))!);

        Assert.Equal("content", revision.Field);
        Assert.Equal("Ứng dụng báo lỗi khi tôi thanh toán.", revision.OldValue);
        Assert.True(revision.OnBehalf);
        Assert.Equal("Bổ sung mã lỗi", revision.Reason);
    }

    /// <summary>
    /// Quyền đọc-tất-cả là quyền **xem**. Kỹ thuật viên có nó để tra cứu bối cảnh, không phải
    /// để viết lại lời khách hàng — nên responder dừng ở 403 dù đọc được phản hồi đó.
    /// </summary>
    [Fact]
    public async Task Quyen_doc_tat_ca_phan_hoi_khong_kem_theo_quyen_sua()
    {
        var customer = await _fx.CustomerAsync();
        var responder = await _fx.ResponderAsync();

        var feedback = await CreateFeedbackAsync(customer, "Phản hồi mà kỹ thuật viên chỉ được đọc.");

        Assert.Equal(HttpStatusCode.OK,
            (await responder.GetAsync($"/api/projects/support/feedbacks/{feedback.Id}")).StatusCode);

        var denied = await responder.PatchAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}",
            new { content = "Kỹ thuật viên tự viết lại lời khách hàng." }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var problem = await denied.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("feedback.respond", problem.GetProperty("requiredPermission").GetString());
    }

    /// <summary>
    /// BR-BIZ-11 áp lên cả đường sửa và đường đọc lịch sử: giá trị cũ của <c>content</c> là
    /// PII đầy đủ, nên để hở <c>/revisions</c> là để hở đúng thứ mà quy tắc kia che.
    ///
    /// 404 chứ không 403 — 403 xác nhận rằng phản hồi đó có thật.
    /// </summary>
    [Fact]
    public async Task Khach_hang_khac_khong_cham_duoc_phan_hoi_lan_lich_su_cua_no()
    {
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();

        var feedback = await CreateFeedbackAsync(alice, "Thông tin riêng tư của khách hàng Alice.");

        var denied = await bob.PatchAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}",
            new { content = "Bob viết lại phản hồi của Alice." }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await bob.GetAsync($"/api/projects/support/feedbacks/{feedback.Id}/revisions")).StatusCode);
    }

    // ---------------- Câu trả lời ----------------

    [Fact]
    public async Task Support_sua_duoc_cau_tra_loi_cua_minh()
    {
        var customer = await _fx.CustomerAsync();
        var support = await _fx.SupportAsync();

        var feedback = await CreateFeedbackAsync(customer, "Phản hồi chờ doanh nghiệp trả lời.");

        var created = await support.PostAsJsonAsync($"/api/projects/support/feedbacks/{feedback.Id}/replies",
            new { body = "Bộ phận kế toán sẽ liên hệ trong hôm nay." }, ApiFactory.Json);
        created.EnsureSuccessStatusCode();
        var reply = (await created.Content.ReadFromJsonAsync<ReplyDto>(ApiFactory.Json))!;

        var updated = await PatchAsync<ReplyDto>(support,
            $"/api/projects/support/feedbacks/{feedback.Id}/replies/{reply.Id}",
            new { body = "Bộ phận kế toán sẽ liên hệ trong hôm nay hoặc sáng mai." });

        Assert.NotNull(updated.LastEdit);

        var revision = Assert.Single((await support.GetFromJsonAsync<List<RevisionDto>>(
            $"/api/projects/support/feedbacks/{feedback.Id}/replies/{reply.Id}/revisions", ApiFactory.Json))!);

        Assert.Equal("Bộ phận kế toán sẽ liên hệ trong hôm nay.", revision.OldValue);
        Assert.False(revision.OnBehalf);
    }

    /// <summary>
    /// Lời xác nhận tự động là bản sao đúng câu hệ thống đã gửi cho khách. Sửa nó là sửa lại
    /// quá khứ, nên ngay cả admin cũng nhận 409.
    /// </summary>
    [Fact]
    public async Task Loi_xac_nhan_tu_dong_khong_ai_sua_duoc()
    {
        var customer = await _fx.CustomerAsync();
        var admin = await _fx.AdminAsync();

        var feedback = await CreateFeedbackAsync(customer, "Phản hồi sinh ra lời xác nhận tự động.");

        var replies = await admin.GetFromJsonAsync<List<ReplyDto>>(
            $"/api/projects/support/feedbacks/{feedback.Id}/replies", ApiFactory.Json);
        var auto = replies!.Single(r => r.IsAutomatic);

        var denied = await admin.PatchAsJsonAsync(
            $"/api/projects/support/feedbacks/{feedback.Id}/replies/{auto.Id}",
            new { body = "Viết lại lời hệ thống đã gửi cho khách." }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
    }

    // ---------------- Helpers ----------------

    private static async Task<T> PatchAsync<T>(HttpClient client, string url, object body)
    {
        var response = await client.PatchAsJsonAsync(url, body, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ApiFactory.Json))!;
    }

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

    private sealed record EditSignatureDto(UserRefDto By, DateTimeOffset At);

    private sealed record RevisionDto(
        Guid Id, string Field, string? OldValue, string? NewValue,
        UserRefDto EditedBy, DateTimeOffset EditedAt, bool OnBehalf, string? Reason);

    private sealed record IncidentDto(
        Guid Id, string Title, string? Description, string Severity, string Status,
        UserRefDto Reporter, EditSignatureDto? LastEdit);

    private sealed record FeedbackDto(Guid Id, string Content, string Status, EditSignatureDto? LastEdit);

    private sealed record CommentDto(Guid Id, string Body, EditSignatureDto? LastEdit);

    private sealed record ReplyDto(Guid Id, bool IsAutomatic, string Body, EditSignatureDto? LastEdit);
}
