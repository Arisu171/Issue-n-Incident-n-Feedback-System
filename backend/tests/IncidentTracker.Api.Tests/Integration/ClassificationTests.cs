using System.Net;
using System.Net.Http.Json;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Phân loại dữ liệu ở mục ảo <c>uncategorized</c>.
///
/// Đợt 6 dựng được mục chưa phân loại nhưng chỉ **xem** được: không có đường nào đưa một sự cố ra
/// khỏi đó. Nhóm test này khoá hành vi của đường ấy, và khoá luôn bất biến mà nó dựa vào —
/// <b>phản hồi và sự cố nó gắn vào luôn cùng một project</b>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ClassificationTests
{
    private readonly ApiFixture _fx;

    public ClassificationTests(ApiFixture fx) => _fx = fx;

    private async Task<string> NewProjectAsync(string prefix)
    {
        var admin = await _fx.AdminAsync();
        var slug = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.PostAsJsonAsync("/api/projects", new { slug, name = "Project " + prefix }, ApiFactory.Json))
            .EnsureSuccessStatusCode();
        return slug;
    }

    private static async Task<string> NewIncidentAsync(HttpClient client, string project, string title)
        => S(await ReadJsonAsync(await client.PostAsJsonAsync(
            $"/api/projects/{project}/incidents", new { title, severity = "Medium" }, ApiFactory.Json)), "id");

    private static async Task<string> NewFeedbackAsync(
        HttpClient client, string project, string content, string? incidentId = null)
        => S(await ReadJsonAsync(await client.PostAsJsonAsync(
            $"/api/projects/{project}/feedbacks",
            new { channel = "Email", content, incidentId }, ApiFactory.Json)), "id");

    /// <summary>
    /// Chuyển sự cố ra khỏi mục chưa phân loại — và <b>phản hồi đã gắn đi theo</b>.
    ///
    /// Bỏ vế thứ hai thì mở phản hồi ở mục chưa phân loại sẽ thấy nó trỏ sang một sự cố đã nằm ở
    /// project khác: không màn hình nào biết bày nó ở đâu.
    /// </summary>
    [Fact]
    public async Task Chuyen_su_co_khoi_muc_chua_phan_loai_keo_theo_phan_hoi_da_gan()
    {
        var admin = await _fx.AdminAsync();
        var slug = await NewProjectAsync("phanloai");

        var incidentId = await NewIncidentAsync(admin, "uncategorized", "Sự cố chưa biết thuộc dự án nào");
        var feedbackId = await NewFeedbackAsync(
            admin, "uncategorized", "Phản hồi đi kèm sự cố chưa phân loại.", incidentId);

        // Thân trả về không mang project (mọi màn hình đều hỏi dữ liệu theo route project, nên
        // nó không cần); phép kiểm thật là hỏi lại theo đúng hai đường dưới đây.
        var moved = await ReadJsonAsync(await admin.PostAsJsonAsync(
            $"/api/projects/uncategorized/incidents/{incidentId}/transfer",
            new { toProject = slug }, ApiFactory.Json));
        Assert.Equal(incidentId, S(moved, "id"));

        // Sự cố đã sang project mới, và không còn ở mục chưa phân loại.
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync($"/api/projects/{slug}/incidents/{incidentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/projects/uncategorized/incidents/{incidentId}")).StatusCode);

        // Phản hồi gắn vào nó đi theo, không bị bỏ lại.
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync($"/api/projects/{slug}/feedbacks/{feedbackId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/projects/uncategorized/feedbacks/{feedbackId}")).StatusCode);

        // Gán nhầm thì trả ngược lại được — đường đi hai chiều.
        (await admin.PostAsJsonAsync(
            $"/api/projects/{slug}/incidents/{incidentId}/transfer",
            new { toProject = "uncategorized" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync($"/api/projects/uncategorized/incidents/{incidentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync($"/api/projects/uncategorized/feedbacks/{feedbackId}")).StatusCode);
    }

    /// <summary>
    /// Phản hồi <b>chưa gắn</b> sự cố thì tự chuyển được; đã gắn rồi thì không — nó phải đi theo
    /// sự cố, nếu không bất biến "cùng project" vỡ ngay tại đây.
    /// </summary>
    [Fact]
    public async Task Phan_hoi_da_gan_su_co_khong_chuyen_rieng_duoc()
    {
        var admin = await _fx.AdminAsync();
        var slug = await NewProjectAsync("phanhoi");

        var loose = await NewFeedbackAsync(admin, "uncategorized", "Phản hồi rời, chưa gắn sự cố nào.");
        (await admin.PostAsJsonAsync(
            $"/api/projects/uncategorized/feedbacks/{loose}/transfer",
            new { toProject = slug }, ApiFactory.Json)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/projects/{slug}/feedbacks/{loose}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/projects/uncategorized/feedbacks/{loose}")).StatusCode);

        var incidentId = await NewIncidentAsync(admin, "uncategorized", "Sự cố giữ phản hồi lại");
        var linked = await NewFeedbackAsync(admin, "uncategorized", "Phản hồi đã gắn vào sự cố.", incidentId);

        var refused = await admin.PostAsJsonAsync(
            $"/api/projects/uncategorized/feedbacks/{linked}/transfer",
            new { toProject = slug }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>
    /// Phân loại khoá theo <c>project.member.manage</c> — đúng bằng điều kiện để **thấy** mục chưa
    /// phân loại, nên không có chuyện thấy mà không sửa được hay sửa được thứ mình không thấy.
    /// </summary>
    [Fact]
    public async Task Nhan_vien_thuong_khong_phan_loai_duoc()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();

        var from = await NewProjectAsync("tu-day");
        var to = await NewProjectAsync("sang-day");
        (await admin.PutAsync($"/api/projects/{from}/members/support/roles/support", null))
            .EnsureSuccessStatusCode();

        var incidentId = await NewIncidentAsync(support, from, "Sự cố của nhân viên thường");

        // Có quyền đọc và sửa trong project mình, nhưng không có quyền đẩy nó đi nơi khác.
        Assert.Equal(HttpStatusCode.OK,
            (await support.GetAsync($"/api/projects/{from}/incidents/{incidentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync(
            $"/api/projects/{from}/incidents/{incidentId}/transfer",
            new { toProject = to }, ApiFactory.Json)).StatusCode);
    }

    /// <summary>
    /// Không gắn được phản hồi vào sự cố ở <b>project khác</b>.
    ///
    /// Người có quyền toàn cục chạm được cả hai project, nên nếu chỗ gắn không đối chiếu project
    /// thì chính họ tạo ra được cặp phản hồi/sự cố lệch project — đúng thứ mà việc chuyển project
    /// dựa vào để kéo phản hồi đi theo.
    /// </summary>
    [Fact]
    public async Task Khong_gan_duoc_phan_hoi_vao_su_co_o_project_khac()
    {
        var admin = await _fx.AdminAsync();
        var a = await NewProjectAsync("ben-a");
        var b = await NewProjectAsync("ben-b");

        var incidentInB = await NewIncidentAsync(admin, b, "Sự cố nằm ở project B");

        // Gắn lúc tạo: 404 chứ không 403 — 403 đã xác nhận sự cố đó có thật.
        var atCreate = await admin.PostAsJsonAsync($"/api/projects/{a}/feedbacks",
            new { channel = "Email", content = "Cố gắn xuyên project ngay lúc tạo.", incidentId = incidentInB },
            ApiFactory.Json);
        Assert.Equal(HttpStatusCode.NotFound, atCreate.StatusCode);

        // Gắn sau cũng vậy.
        var feedbackInA = await NewFeedbackAsync(admin, a, "Phản hồi ở project A, gắn sau.");
        var afterwards = await admin.PostAsJsonAsync($"/api/projects/{a}/feedbacks/{feedbackInA}/link",
            new { incidentId = incidentInB }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.NotFound, afterwards.StatusCode);

        // Còn gắn vào sự cố cùng project thì vẫn bình thường.
        var incidentInA = await NewIncidentAsync(admin, a, "Sự cố cùng project");
        (await admin.PostAsJsonAsync($"/api/projects/{a}/feedbacks/{feedbackInA}/link",
            new { incidentId = incidentInA }, ApiFactory.Json)).EnsureSuccessStatusCode();
    }
}
