using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Kiểm thử âm cho phân quyền theo chủ sở hữu (BOLA) và cho luồng khách hàng tự phục vụ.
///
/// Kiểm thử dương chỉ chứng minh tính năng chạy. Chỉ những ca dưới đây chứng minh hệ thống
/// biết <b>từ chối</b> — mà từ chối mới là toàn bộ giá trị của lớp phân quyền theo bản ghi.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OwnershipAndCustomerTests
{
    private readonly ApiFixture _fx;

    public OwnershipAndCustomerTests(ApiFixture fx) => _fx = fx;

    // ---------------- BOLA trên chiều đọc ----------------

    /// <summary>
    /// <b>Đổi có chủ đích ở đợt 6b.</b> Khách hàng giờ xem được sự cố của khách hàng khác — nhưng
    /// chỉ trong project mà vai trò customer được cấp, và ranh giới thật đã chuyển thành ranh giới
    /// project (xem <c>ProjectIsolationTests</c>).
    ///
    /// Trước đây test này khẳng định điều ngược lại. Giữ nguyên nó thì bộ test sẽ canh một quy tắc
    /// mà sản phẩm không còn theo — tệ hơn không có test, vì nó nói dối về hành vi thật.
    /// </summary>
    [Fact]
    public async Task Khach_hang_doc_duoc_su_co_cua_khach_hang_khac_trong_cung_project()
    {
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();

        var incident = await CreateIncidentAsync(alice, "Sự cố của khách hàng A");

        Assert.Equal(HttpStatusCode.OK,
            (await bob.GetAsync($"/api/projects/support/incidents/{incident.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await bob.GetAsync($"/api/projects/support/incidents/{incident.Id}/history")).StatusCode);
    }

    /// <summary>
    /// Nhưng **ghi chú nội bộ** thì vẫn kín — đó mới là thứ nhân viên viết cho nhau đọc, và nó
    /// khoá theo <c>incident.note.read</c> chứ không theo quyền xem sự cố.
    /// </summary>
    [Fact]
    public async Task Khach_hang_van_khong_doc_duoc_ghi_chu_noi_bo()
    {
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(alice, "Ghi chú nội bộ vẫn kín");
        (await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating", note = "Phân tích nội bộ, chưa báo khách." },
            ApiFactory.Json)).EnsureSuccessStatusCode();

        var history = await bob.GetFromJsonAsync<JsonElement>(
            $"/api/projects/support/incidents/{incident.Id}/history", ApiFactory.Json);
        var row = history.EnumerateArray().Single();
        Assert.Equal("Mitigating", row.GetProperty("toStatus").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("note").ValueKind);
    }

    /// <summary>
    /// Danh sách phải lọc ngay trong SQL. Lọc sau khi nạp thì totalCount vẫn đếm cả sự cố ngoài
    /// phạm vi — vẫn là rò rỉ, chỉ là rò ít hơn.
    ///
    /// Từ đợt 6b, phạm vi của khách hàng là **project**, không còn là "chỉ của tôi".
    /// </summary>
    [Fact]
    public async Task TCBIZ10_danh_sach_cua_khach_hang_dung_bang_tong_so_dem_duoc()
    {
        var admin = await _fx.AdminAsync();
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();

        // Project riêng cho test này: `support` là project dùng chung, số sự cố trong đó phụ
        // thuộc mọi test khác nên phép so `Items.Count == TotalCount` sẽ đúng hay sai tuỳ thứ tự
        // chạy — một test như vậy không chứng minh được gì.
        var slug = "dem-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.PostAsJsonAsync("/api/projects", new { slug, name = "Đếm" }, ApiFactory.Json))
            .EnsureSuccessStatusCode();
        (await admin.PutAsync($"/api/projects/{slug}/role-access/customer", null)).EnsureSuccessStatusCode();

        // Mở cho vai trò mới là vế thứ nhất; hai khách này còn phải tự nhận project.
        (await alice.PutAsync($"/api/project-catalog/{slug}", null)).EnsureSuccessStatusCode();
        (await bob.PutAsync($"/api/project-catalog/{slug}", null)).EnsureSuccessStatusCode();

        var ofAlice = await CreateIncidentAsync(alice, "Của A", project: slug);
        var ofBob = await CreateIncidentAsync(bob, "Của B", project: slug);

        var page = await bob.GetFromJsonAsync<PagedDto<IncidentDto>>(
            $"/api/projects/{slug}/incidents?pageSize=100", ApiFactory.Json);

        Assert.Contains(page!.Items, i => i.Id == ofBob.Id);
        Assert.Contains(page.Items, i => i.Id == ofAlice.Id);
        // Con số tổng phải khớp với số dòng thật sự trả về, không đếm rộng hơn.
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(page.Items.Count, page.TotalCount);
    }

    /// <summary>Support có <c>incident.read.all</c> nên vẫn thấy sự cố của mọi khách hàng.</summary>
    [Fact]
    public async Task Support_van_doc_duoc_su_co_cua_khach_hang()
    {
        var alice = await _fx.CustomerAsync();
        var support = await _fx.SupportAsync();

        var incident = await CreateIncidentAsync(alice, "Support phải thấy được");

        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync($"/api/projects/support/incidents/{incident.Id}")).StatusCode);
    }

    // ---------------- BOLA trên chiều ghi ----------------

    /// <summary>
    /// BR-BIZ-10 · TC-BIZ-11 — trọng tâm của bài. Responder thứ hai có đủ permission
    /// <c>incident.update_status</c> nhưng sự cố đã do người khác nhận, nên phải bị chặn.
    /// Đây chính là ca mà role check một mình không bao giờ bắt được.
    /// </summary>
    [Fact]
    public async Task TCBIZ11_nguoi_khong_duoc_giao_khong_chuyen_duoc_trang_thai()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var mitigator = await _fx.MitigatorAsync();

        var incident = await CreateIncidentAsync(support, "Chỉ người được giao mới xử lý");

        // Responder thao tác trước nên tự nhận việc.
        var first = await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Mitigator cũng có incident.update_status nhưng không phải người được giao.
        var second = await mitigator.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        Assert.Equal("incident.manage_any", problem.GetProperty("requiredPermission").GetString());
    }

    /// <summary>Sự cố chưa ai nhận thì người thao tác đầu tiên trở thành người phụ trách.</summary>
    [Fact]
    public async Task Nguoi_thao_tac_dau_tien_tu_nhan_viec()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(support, "Chưa ai nhận thì ai vào trước người đó nhận");
        Assert.Null(incident.Assignee);

        await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);

        var me = await responder.GetFromJsonAsync<MeDto>("/api/auth/me", ApiFactory.Json);
        var after = await responder.GetFromJsonAsync<IncidentDto>(
            $"/api/projects/support/incidents/{incident.Id}", ApiFactory.Json);

        Assert.Equal(me!.Id, after!.Assignee!.Id);
    }

    /// <summary>Admin có <c>incident.manage_any</c> nên vượt qua được ràng buộc sở hữu.</summary>
    [Fact]
    public async Task Admin_van_thao_tac_duoc_tren_su_co_cua_nguoi_khac()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var admin = await _fx.AdminAsync();

        var incident = await CreateIncidentAsync(support, "Admin đi xuyên qua ràng buộc sở hữu");
        await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);

        var response = await admin.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Resolved" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------- Khách hàng tự phục vụ ----------------

    /// <summary>
    /// UC-BIZ-08 — khách hàng đăng nhập, gửi sự cố, theo dõi được sự cố đó, nhưng không tự
    /// chuyển trạng thái được. Vòng đời vẫn thuộc về đội xử lý.
    /// </summary>
    [Fact]
    public async Task TCBIZ12_khach_hang_gui_va_theo_doi_duoc_su_co_cua_minh()
    {
        var customer = await _fx.CustomerAsync();

        var incident = await CreateIncidentAsync(customer, "Khách hàng tự gửi ticket");

        Assert.Equal("Investigating", incident.Status);

        var me = await customer.GetFromJsonAsync<MeDto>("/api/auth/me", ApiFactory.Json);
        Assert.Equal(me!.Id, incident.Reporter.Id);

        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/projects/support/incidents/{incident.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await customer.GetAsync($"/api/projects/support/incidents/{incident.Id}/history")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PatchAsJsonAsync(
            $"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json)).StatusCode);
    }

    /// <summary>Khách hàng vẫn theo dõi được sự cố của mình sau khi đội xử lý đã nhận việc.</summary>
    [Fact]
    public async Task Khach_hang_van_theo_doi_duoc_sau_khi_responder_nhan_viec()
    {
        var customer = await _fx.CustomerAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(customer, "Theo dõi tiến độ xử lý");
        await responder.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
            new { targetStatus = "Mitigating" }, ApiFactory.Json);

        var after = await customer.GetFromJsonAsync<IncidentDto>(
            $"/api/projects/support/incidents/{incident.Id}", ApiFactory.Json);

        Assert.Equal("Mitigating", after!.Status);
    }

    /// <summary>BR-BIZ-11 — phản hồi kèm PII của khách hàng khác không được lộ.</summary>
    [Fact]
    public async Task TCBIZ13_khach_hang_khong_doc_duoc_phan_hoi_cua_nguoi_khac()
    {
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();

        var created = await alice.PostAsJsonAsync("/api/projects/support/feedbacks",
            new { channel = "Web", customerEmail = "alice@khachhang.vn", content = "Nội dung riêng tư của A." },
            ApiFactory.Json);
        created.EnsureSuccessStatusCode();
        var feedback = await created.Content.ReadFromJsonAsync<JsonElement>(ApiFactory.Json);
        var feedbackId = feedback.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync($"/api/projects/support/feedbacks/{feedbackId}")).StatusCode);

        var page = await bob.GetFromJsonAsync<PagedDto<JsonElement>>(
            "/api/projects/support/feedbacks?pageSize=100", ApiFactory.Json);
        Assert.DoesNotContain(page!.Items, f => f.GetProperty("id").GetGuid() == feedbackId);
    }

    /// <summary>
    /// BOLA chiều ghi trên Feedback: không được móc phản hồi của mình vào sự cố người khác.
    /// Khách hàng không có <c>feedback.link</c> nên đi bằng trường <c>incidentId</c> lúc tạo.
    /// </summary>
    [Fact]
    public async Task TCBIZ13_khach_hang_khong_gan_duoc_phan_hoi_vao_su_co_nguoi_khac()
    {
        var alice = await _fx.CustomerAsync();
        var bob = await _fx.OtherCustomerAsync();

        var incidentOfAlice = await CreateIncidentAsync(alice, "Sự cố của A, B đừng đụng vào");

        var response = await bob.PostAsJsonAsync("/api/projects/support/feedbacks",
            new
            {
                channel = "Web",
                content = "Cố gắn vào sự cố của người khác.",
                incidentId = incidentOfAlice.Id
            }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------------- Helpers ----------------

    private static async Task<IncidentDto> CreateIncidentAsync(HttpClient client, string title, string project = "support")
    {
        var response = await client.PostAsJsonAsync($"/api/projects/{project}/incidents",
            new { title, severity = "Medium" }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<IncidentDto>(ApiFactory.Json))!;
    }

    private sealed record UserRefDto(Guid Id, string DisplayName, string Email);

    private sealed record IncidentDto(
        Guid Id, string Title, string Status, UserRefDto Reporter, UserRefDto? Assignee);

    private sealed record PagedDto<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

    private sealed record MeDto(Guid Id, string Email);
}
