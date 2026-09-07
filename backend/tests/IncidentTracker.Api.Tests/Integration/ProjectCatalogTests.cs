using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Khách hàng tự chọn project mình đang dùng dịch vụ.
///
/// Quyền của vai trò tự phục vụ là <b>giao</b> của hai vế: quản trị mở project cho vai trò
/// (<c>project_role_access</c>), và người dùng tự nhận (<c>project_subscriptions</c>). Nhóm test
/// này khoá vế then chốt — <b>ô tick không phải một đường tự cấp quyền</b>.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProjectCatalogTests
{
    private readonly ApiFixture _fx;

    public ProjectCatalogTests(ApiFixture fx) => _fx = fx;

    private async Task<string> NewBareProjectAsync(string prefix)
    {
        var admin = await _fx.AdminAsync();
        var slug = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.PostAsJsonAsync("/api/projects", new { slug, name = "Project " + prefix }, ApiFactory.Json))
            .EnsureSuccessStatusCode();
        return slug;
    }

    private static async Task<List<string>> CatalogSlugsAsync(HttpClient client)
        => (await ReadJsonAsync(await client.GetAsync("/api/project-catalog")))
            .EnumerateArray().Select(p => S(p, "slug")).ToList();

    /// <summary>
    /// Điều mà cả cơ chế này sinh ra để bảo đảm: tự nhận một project <b>chưa mở</b> thì không tự
    /// nhận được, và câu trả lời là 404 chứ không 403 — 403 đã xác nhận project đó có thật.
    /// </summary>
    [Fact]
    public async Task Khong_tu_nhan_duoc_project_chua_mo_cho_minh()
    {
        var customer = await _fx.CustomerAsync();
        var closed = await NewBareProjectAsync("chua-mo");

        Assert.DoesNotContain(closed, await CatalogSlugsAsync(customer));

        Assert.Equal(HttpStatusCode.NotFound,
            (await customer.PutAsync($"/api/project-catalog/{closed}", null)).StatusCode);

        // Và không tự nhận được thì cũng không vào được — không có đường vòng nào khác.
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/projects/{closed}/tickets")).StatusCode);
    }

    /// <summary>
    /// Danh mục chỉ liệt kê project đã mở cho vai trò của chính người gọi.
    ///
    /// Không phải để giao diện gọn: <b>tên project là dữ liệu</b>. Một danh sách đầy đủ mọi project
    /// kèm ô tick mờ là đã nói cho người ngoài biết hệ thống đang có những gì.
    /// </summary>
    [Fact]
    public async Task Danh_muc_khong_lo_ten_project_chua_mo()
    {
        var admin = await _fx.AdminAsync();
        var customer = await _fx.CustomerAsync();

        var opened = await NewBareProjectAsync("mo-ra");
        var hidden = await NewBareProjectAsync("giau-di");
        (await admin.PutAsync($"/api/projects/{opened}/role-access/customer", null)).EnsureSuccessStatusCode();

        var slugs = await CatalogSlugsAsync(customer);
        Assert.Contains(opened, slugs);
        Assert.DoesNotContain(hidden, slugs);
    }

    /// <summary>
    /// Nhân viên <b>không</b> phải tự nhận: cấp thành viên là vào được ngay.
    ///
    /// Luật giao-của-hai-vế chỉ áp cho vai trò tự phục vụ. Áp cho mọi vai trò thì admin cấp
    /// role-access cho `support` xong cả nhóm vẫn đứng ngoài cho tới khi từng người tự tick —
    /// hành vi không ai ngờ tới, và không ai biết phải đi tick ở đâu.
    /// </summary>
    [Fact]
    public async Task Nhan_vien_khong_phai_tu_nhan()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();
        var slug = await NewBareProjectAsync("nhanvien");

        (await admin.PutAsync($"/api/projects/{slug}/members/support/roles/support", null))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync($"/api/projects/{slug}/tickets")).StatusCode);
        // Và danh mục tự chọn không dành cho họ.
        Assert.DoesNotContain(slug, await CatalogSlugsAsync(support));
    }

    /// <summary>
    /// Tick hai lần, hoặc hai tab cùng gửi, không được thành lỗi — và bỏ tick khi chưa tick cũng
    /// vậy. Ô tick là trạng thái mong muốn, không phải một sự kiện đếm được.
    /// </summary>
    [Fact]
    public async Task Tu_nhan_va_roi_deu_lam_lai_duoc()
    {
        var admin = await _fx.AdminAsync();
        var customer = await _fx.CustomerAsync();
        var slug = await NewBareProjectAsync("lap-lai");
        (await admin.PutAsync($"/api/projects/{slug}/role-access/customer", null)).EnsureSuccessStatusCode();

        // Rời khi chưa từng tham gia.
        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"/api/project-catalog/{slug}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await customer.PutAsync($"/api/project-catalog/{slug}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.PutAsync($"/api/project-catalog/{slug}", null)).StatusCode);

        var row = (await ReadJsonAsync(await customer.GetAsync("/api/project-catalog")))
            .EnumerateArray().Single(p => S(p, "slug") == slug);
        Assert.True(row.GetProperty("joined").GetBoolean());
    }

    /// <summary>
    /// Rời project là <b>mất quyền xem</b>, không phải xoá dữ liệu: quay lại thì sự cố đã gửi vẫn
    /// còn nguyên. Nhầm hai điều này thì một cú bỏ tick thành một cú xoá dữ liệu không hoàn tác được.
    /// </summary>
    [Fact]
    public async Task Roi_project_khong_lam_mat_du_lieu_da_gui()
    {
        var admin = await _fx.AdminAsync();
        var customer = await _fx.CustomerAsync();
        var slug = await NewBareProjectAsync("giu-du-lieu");
        (await admin.PutAsync($"/api/projects/{slug}/role-access/customer", null)).EnsureSuccessStatusCode();
        (await customer.PutAsync($"/api/project-catalog/{slug}", null)).EnsureSuccessStatusCode();

        var incident = await ReadJsonAsync(await customer.PostAsJsonAsync($"/api/projects/{slug}/incidents",
            new { title = "Sự cố gửi trước khi rời", severity = "Low" }, ApiFactory.Json));
        var id = S(incident, "id");

        (await customer.DeleteAsync($"/api/project-catalog/{slug}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/projects/{slug}/incidents/{id}")).StatusCode);

        // Nhân viên vẫn thấy nguyên: dữ liệu không đi đâu cả.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/projects/{slug}/incidents/{id}")).StatusCode);

        // Và khách quay lại thì thấy lại chính sự cố đó.
        (await customer.PutAsync($"/api/project-catalog/{slug}", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/projects/{slug}/incidents/{id}")).StatusCode);
    }

    /// <summary>
    /// Project lưu trữ rời khỏi danh mục: bày ra một lựa chọn dẫn tới project không còn hoạt động
    /// là bày ra lựa chọn không dẫn tới đâu.
    /// </summary>
    [Fact]
    public async Task Project_luu_tru_khong_con_trong_danh_muc()
    {
        var admin = await _fx.AdminAsync();
        var customer = await _fx.CustomerAsync();
        var slug = await NewBareProjectAsync("luu-tru");
        (await admin.PutAsync($"/api/projects/{slug}/role-access/customer", null)).EnsureSuccessStatusCode();
        Assert.Contains(slug, await CatalogSlugsAsync(customer));

        (await admin.PatchAsJsonAsync($"/api/projects/{slug}", new { isArchived = true }, ApiFactory.Json))
            .EnsureSuccessStatusCode();

        Assert.DoesNotContain(slug, await CatalogSlugsAsync(customer));
        Assert.Equal(HttpStatusCode.NotFound,
            (await customer.PutAsync($"/api/project-catalog/{slug}", null)).StatusCode);
    }
}
