using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using IncidentTracker.Api.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Quyền gắn theo project (đợt 6).
///
/// Trước đây vai trò là toàn cục: một tài khoản `responder` làm được mọi việc ở **mọi** project.
/// Giờ quyền đến từ hai nguồn — <c>project_members</c> cho từng người, <c>project_role_access</c>
/// cho cả vai trò — và chỉ vai trò <c>admin</c> còn hiệu lực toàn hệ thống.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProjectIsolationTests
{
    private readonly ApiFixture _fx;

    public ProjectIsolationTests(ApiFixture fx) => _fx = fx;

    private async Task<string> NewBareProjectAsync(string prefix)
    {
        var admin = await _fx.AdminAsync();
        var slug = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.PostAsJsonAsync("/api/projects", new { slug, name = "Project " + prefix }, ApiFactory.Json))
            .EnsureSuccessStatusCode();
        return slug;
    }

    /// <summary>
    /// Điều mà cả đợt 6 sinh ra để bảo đảm: có vai trò không còn là có quyền ở mọi nơi.
    /// </summary>
    [Fact]
    public async Task Nhan_vien_khong_cham_duoc_project_minh_khong_thuoc_ve()
    {
        var admin = await _fx.AdminAsync();
        var responder = await _fx.ResponderAsync();

        var mine = await NewBareProjectAsync("mine");
        var theirs = await NewBareProjectAsync("theirs");

        // Chưa được cấp: không đọc được project nào trong hai cái.
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/projects/{mine}/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/projects/{theirs}/tickets")).StatusCode);

        // Cấp vào đúng một project.
        (await admin.PutAsync($"/api/projects/{mine}/members/responder/roles/responder", null))
            .EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await responder.GetAsync($"/api/projects/{mine}/tickets")).StatusCode);
        // Và vẫn không chạm được cái kia — đây là dòng chứng minh việc cách ly có thật.
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/projects/{theirs}/tickets")).StatusCode);

        // Ghi cũng vậy, không riêng đọc.
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.PostAsJsonAsync(
            $"/api/projects/{theirs}/labels", new { name = "chen-ngang", color = "#ff0000" }, ApiFactory.Json)).StatusCode);

        // Gỡ ra thì mất quyền ngay, không chờ token hết hạn.
        (await admin.DeleteAsync($"/api/projects/{mine}/members/responder/roles/responder"))
            .EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.GetAsync($"/api/projects/{mine}/tickets")).StatusCode);
    }

    /// <summary>
    /// Khách hàng cấp theo **vai trò**, không theo từng người: với hàng nghìn khách thì cấp từng
    /// người là việc không ai làm nổi.
    ///
    /// Nhưng cấp cho vai trò mới chỉ **mở** project ra thành danh mục — khách còn phải tự nhận
    /// project mình đang dùng dịch vụ. Quyền thật là <b>giao</b> của hai vế, và test này khoá cả
    /// hai chiều: thiếu vế nào cũng không vào được.
    /// </summary>
    [Fact]
    public async Task Khach_hang_cap_theo_vai_tro_chu_khong_theo_tung_nguoi()
    {
        var admin = await _fx.AdminAsync();
        var customer = await _fx.CustomerAsync();
        var other = await _fx.OtherCustomerAsync();
        var slug = await NewBareProjectAsync("khach");

        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/projects/{slug}/tickets")).StatusCode);

        // Chưa tự nhận thì mở rồi vẫn chưa vào được: đây là vế thứ hai.
        (await admin.PutAsync($"/api/projects/{slug}/role-access/customer", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/projects/{slug}/tickets")).StatusCode);

        // Project đã mở thì hiện trong danh mục của khách, kèm cờ chưa tham gia.
        var catalog = await ReadJsonAsync(await customer.GetAsync("/api/project-catalog"));
        var row = catalog.EnumerateArray().Single(p => S(p, "slug") == slug);
        Assert.False(row.GetProperty("joined").GetBoolean());

        // Tự nhận xong thì vào được ngay, không chờ token hết hạn.
        (await customer.PutAsync($"/api/project-catalog/{slug}", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/projects/{slug}/tickets")).StatusCode);

        // Cấp **một lần** cho cả vai trò: khách thứ hai chỉ cần tự nhận, không ai phải cấp riêng.
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/projects/{slug}/tickets")).StatusCode);
        (await other.PutAsync($"/api/project-catalog/{slug}", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/projects/{slug}/tickets")).StatusCode);

        // Tự rời thì mất quyền, nhưng vế của quản trị vẫn còn nên vào lại được.
        (await other.DeleteAsync($"/api/project-catalog/{slug}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/projects/{slug}/tickets")).StatusCode);

        // Gỡ vế của quản trị: đã tự nhận rồi cũng không cứu được — ô tick không phải đường tự cấp.
        (await admin.DeleteAsync($"/api/projects/{slug}/role-access/customer")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync($"/api/projects/{slug}/tickets")).StatusCode);

        // Và project đã đóng thì rời khỏi danh mục — không tự nhận lại được.
        Assert.Empty((await ReadJsonAsync(await customer.GetAsync("/api/project-catalog")))
            .EnumerateArray().Where(p => S(p, "slug") == slug));
        Assert.Equal(HttpStatusCode.NotFound,
            (await customer.PutAsync($"/api/project-catalog/{slug}", null)).StatusCode);
    }

    /// <summary>
    /// Vai trò <c>system</c> đứng trên admin và giữ **cấu hình nền của cả hệ thống**.
    ///
    /// Ranh giới phải kiểm được bằng hành vi chứ không chỉ nằm trong bảng phân quyền: một admin
    /// thường vận hành được tài khoản, RBAC và project, nhưng không đổi được chính sách SLA hay
    /// bộ loại issue — hai thứ mà đổi một lần là đổi cho mọi project cùng lúc.
    /// </summary>
    [Fact]
    public async Task Cau_hinh_nen_he_thong_thuoc_ve_role_system()
    {
        // Tạo admin phải do `system` làm: luật rank chặn việc cấp role ngang cấp mình,
        // nên tài khoản admin (level 100) không tự sinh ra admin (rank 100) được.
        var bootstrap = await _fx.SystemAsync();

        // Admin thường: mang đúng `admin`, không kèm `system`.
        var email = $"adm-{Guid.NewGuid():N}"[..20] + "@test.local";
        (await bootstrap.PostAsJsonAsync("/api/users",
            new { email, displayName = "Quản trị thường", password = ApiFixture.Password, roles = new[] { "admin" } },
            ApiFactory.Json)).EnsureSuccessStatusCode();
        var admin = await _fx.Factory.CreateClientAsAsync(email, ApiFixture.Password);

        // Vẫn làm được việc của admin.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/roles")).StatusCode);

        // Nhưng không chạm được cấu hình nền.
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/issue-types",
            new { name = "Chen ngang", description = "" }, ApiFactory.Json)).StatusCode);

        // Còn tài khoản mang `system` thì làm được — nếu không thì chẳng ai cấu hình nổi.
        Assert.Equal(HttpStatusCode.OK, (await bootstrap.GetAsync("/api/issue-types")).StatusCode);
        var created = await bootstrap.PostAsJsonAsync("/api/issue-types",
            new { name = "Loai-" + Guid.NewGuid().ToString("N")[..6], description = "" }, ApiFactory.Json);
        Assert.True(created.IsSuccessStatusCode, $"system phải tạo được loại issue, nhận {created.StatusCode}");
    }

    /// <summary>
    /// Tạo dự án sẵn ở trạng thái lưu trữ.
    ///
    /// Màn hình tạo và màn hình sửa bày ra cùng một tập lựa chọn, nên cờ này phải đi thẳng trong
    /// lời gọi tạo: tách thành POST rồi PATCH thì lời thứ hai hỏng là để lại một dự án nửa vời,
    /// đang hiện ra cho mọi người trong khi người tạo tưởng nó đã ẩn.
    /// </summary>
    [Fact]
    public async Task Tao_duoc_du_an_o_trang_thai_luu_tru()
    {
        var admin = await _fx.AdminAsync();
        var slug = "luutru-" + Guid.NewGuid().ToString("N")[..8];

        var created = await ReadJsonAsync(await admin.PostAsJsonAsync("/api/projects",
            new { slug, name = "Giữ chỗ", isArchived = true }, ApiFactory.Json));
        Assert.True(created.GetProperty("isArchived").GetBoolean());

        // Lưu trữ nghĩa là biến khỏi danh sách mặc định, không phải biến mất.
        var visible = await ReadJsonAsync(await admin.GetAsync("/api/projects"));
        Assert.DoesNotContain(visible.EnumerateArray(), p => S(p, "slug") == slug);

        var all = await ReadJsonAsync(await admin.GetAsync("/api/projects?includeArchived=true"));
        Assert.Contains(all.EnumerateArray(), p => S(p, "slug") == slug);

        // Mặc định vẫn là không lưu trữ — cờ này phải do người dùng bật.
        var normal = await ReadJsonAsync(await admin.PostAsJsonAsync("/api/projects",
            new { slug = slug + "-b", name = "Bình thường" }, ApiFactory.Json));
        Assert.False(normal.GetProperty("isArchived").GetBoolean());
    }

    /// <summary>Admin là quyền toàn cục: không phải thêm vào từng project mới quản trị được.</summary>
    [Fact]
    public async Task Admin_van_vao_duoc_moi_project_ma_khong_can_cap()
    {
        var admin = await _fx.AdminAsync();
        var slug = await NewBareProjectAsync("adm");

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/projects/{slug}/tickets")).StatusCode);
        var access = await admin.GetFromJsonAsync<JsonElement>($"/api/projects/{slug}/members", ApiFactory.Json);
        // Người tạo (admin) được thêm làm thành viên, nhưng quyền của admin không dựa vào đó.
        Assert.NotEqual(0, access.GetProperty("members").GetArrayLength());
    }

    /// <summary>
    /// Manager quản lý **thành viên project**, không quản lý tài khoản.
    ///
    /// Bảng <c>users</c> là toàn cục: cho manager của một project quyền xoá tài khoản thì họ vô
    /// hiệu hoá được người đang làm ở project khác — leo thang đặc quyền đi vòng.
    /// </summary>
    [Fact]
    public async Task Manager_quan_ly_thanh_vien_nhung_khong_dung_toi_tai_khoan()
    {
        var admin = await _fx.AdminAsync();
        var slug = await NewBareProjectAsync("mgr");

        var email = $"mgr-{Guid.NewGuid():N}"[..20] + "@test.local";
        (await admin.PostAsJsonAsync("/api/users",
            new { email, displayName = "Quản lý", password = ApiFixture.Password, roles = Array.Empty<string>() },
            ApiFactory.Json)).EnsureSuccessStatusCode();

        await using (var db = _fx.Factory.CreateDbContext())
        {
            var login = email.Split('@')[0];
            var userId = await db.Users.Where(u => u.Login == login).Select(u => u.Id).FirstAsync();
            var projectId = await db.Projects.Where(p => p.Slug == slug).Select(p => p.Id).FirstAsync();
            var roleId = await db.Roles.Where(r => r.Name == "manager").Select(r => r.Id).FirstAsync();
            db.ProjectMembers.Add(new Api.Domain.ProjectMember
            {
                ProjectId = projectId, UserId = userId, RoleId = roleId, AddedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var manager = await _fx.Factory.CreateClientAsAsync(email, ApiFixture.Password);

        // Trong project của mình: thêm được thành viên.
        Assert.Equal(HttpStatusCode.NoContent,
            (await manager.PutAsync($"/api/projects/{slug}/members/support/roles/support", null)).StatusCode);

        // Nhưng không đụng được tới bảng tài khoản toàn cục.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await manager.DeleteAsync($"/api/users/{Guid.NewGuid()}")).StatusCode);

        // Và không cấp được vai trò ngang hoặc cao hơn cấp của mình (BR-SEC-08 vẫn áp).
        Assert.Equal(HttpStatusCode.Forbidden,
            (await manager.PutAsync($"/api/projects/{slug}/members/support/roles/manager", null)).StatusCode);

        // Cũng không quản lý được project khác.
        var elsewhere = await NewBareProjectAsync("elsewhere");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await manager.PutAsync($"/api/projects/{elsewhere}/members/support/roles/support", null)).StatusCode);
    }

    /// <summary>
    /// Màn hình xuyên project phải **cắt dữ liệu**, không chỉ chặn đường vào.
    ///
    /// Search, danh sách board và hộp thư đều cố ý xuyên project — chặn hẳn thì mất công dụng.
    /// Nhưng nếu chúng không lọc theo quyền thì chỉ cần gõ vào ô tìm kiếm là đọc được tiêu đề và
    /// nội dung ticket của project mình không có quyền, và toàn bộ đợt 6 thành vô nghĩa.
    /// </summary>
    [Fact]
    public async Task Man_hinh_xuyen_project_cat_du_lieu_theo_quyen()
    {
        var admin = await _fx.AdminAsync();
        var responder = await _fx.ResponderAsync();

        var mine = await NewBareProjectAsync("cat-cua-toi");
        var theirs = await NewBareProjectAsync("cat-cua-nguoi");
        (await admin.PutAsync($"/api/projects/{mine}/members/responder/roles/responder", null))
            .EnsureSuccessStatusCode();

        var token = "zzcatdulieu" + Guid.NewGuid().ToString("N")[..6];
        await CreateTicketAsync(admin, $"{token} thay duoc", project: mine);
        await CreateTicketAsync(admin, $"{token} khong thay duoc", project: theirs);

        // So theo **project**, không theo số hiệu: số ticket đánh riêng cho từng project nên cả
        // hai đều là #1, và một phép so theo số sẽ xanh hoặc đỏ vì lý do hoàn toàn khác.
        var found = await ReadJsonAsync(await responder.GetAsync($"/api/search/tickets?q={token}"));
        var slugs = found.GetProperty("items").EnumerateArray().Select(x => S(x, "projectSlug")).ToList();
        Assert.Contains(mine, slugs);
        Assert.DoesNotContain(theirs, slugs);

        // Không chỉ project — tiêu đề cũng không được lọt ra.
        var titles = found.GetProperty("items").EnumerateArray().Select(x => S(x, "title")).ToList();
        Assert.DoesNotContain(titles, t => t.Contains("khong thay duoc", StringComparison.Ordinal));

        // Admin thì thấy cả hai — quyền toàn cục không bị cắt.
        var all = await ReadJsonAsync(await admin.GetAsync($"/api/search/tickets?q={token}"));
        Assert.Equal(2, all.GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// Sự cố và phản hồi cách ly theo project, và mục ảo <c>uncategorized</c> hoạt động.
    /// </summary>
    [Fact]
    public async Task Su_co_cach_ly_theo_project_va_muc_chua_phan_loai()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();

        var slug = await NewBareProjectAsync("sc");
        (await admin.PutAsync($"/api/projects/{slug}/members/support/roles/support", null))
            .EnsureSuccessStatusCode();

        var created = await ReadJsonAsync(await support.PostAsJsonAsync(
            $"/api/projects/{slug}/incidents",
            new { title = "Sự cố trong project riêng", severity = "High" }, ApiFactory.Json));
        var id = S(created, "id");

        // Đọc trong đúng project thì được.
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync($"/api/projects/{slug}/incidents/{id}")).StatusCode);

        // Ghép id vào slug khác — đường vòng hiển nhiên nhất — phải trượt, và trả **404** chứ
        // không 403: 403 đã xác nhận sự cố đó có thật.
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/projects/support/incidents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/projects/support/incidents/{id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/projects/support/incidents/{id}/comments")).StatusCode);

        // Danh sách của project khác không chứa nó.
        var elsewhere = await ReadJsonAsync(await admin.GetAsync("/api/projects/support/incidents?pageSize=100"));
        Assert.DoesNotContain(elsewhere.GetProperty("items").EnumerateArray(), x => S(x, "id") == id);

        // Mục chưa phân loại: admin vào được, nhân viên thường thì không.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/projects/uncategorized/incidents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await support.GetAsync("/api/projects/uncategorized/incidents")).StatusCode);

        // Và không ai tạo được project trùng tên với mục ảo đó.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/projects",
            new { slug = "uncategorized", name = "Chiếm chỗ" }, ApiFactory.Json)).StatusCode);
    }

    /// <summary>
    /// Đợt 6b — khách hàng xem được mọi sự cố **trong project mình với tới**, nhưng ghi chú nội
    /// bộ của nhân viên vẫn kín.
    ///
    /// Đây là chỗ hai quyết định gặp nhau: cấp <c>incident.read.all</c> cho customer chỉ an toàn
    /// vì phân quyền theo project đã cắt sẵn phạm vi, và vì ghi chú đã tách sang
    /// <c>incident.note.read</c> ở đợt 3.
    /// </summary>
    [Fact]
    public async Task Khach_hang_xem_moi_su_co_trong_project_minh_nhung_khong_thay_ghi_chu()
    {
        var admin = await _fx.AdminAsync();
        var customer = await _fx.CustomerAsync();
        var other = await _fx.OtherCustomerAsync();
        var responder = await _fx.ResponderAsync();

        var mine = await NewBareProjectAsync("kh-thay");
        var theirs = await NewBareProjectAsync("kh-khong-thay");
        (await admin.PutAsync($"/api/projects/{mine}/role-access/customer", null)).EnsureSuccessStatusCode();
        (await admin.PutAsync($"/api/projects/{mine}/members/responder/roles/responder", null)).EnsureSuccessStatusCode();

        // Hai khách này đang dùng dịch vụ của project — vế thứ hai của quyền khách hàng.
        (await customer.PutAsync($"/api/project-catalog/{mine}", null)).EnsureSuccessStatusCode();
        (await other.PutAsync($"/api/project-catalog/{mine}", null)).EnsureSuccessStatusCode();

        // Sự cố do khách hàng **khác** gửi: trước đây customer không thấy, giờ thấy.
        var incident = await ReadJsonAsync(await other.PostAsJsonAsync($"/api/projects/{mine}/incidents",
            new { title = "Của khách hàng khác", severity = "Medium" }, ApiFactory.Json));
        var id = S(incident, "id");

        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync($"/api/projects/{mine}/incidents/{id}")).StatusCode);

        // Nhưng chỉ trong project mình với tới.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await customer.GetAsync($"/api/projects/{theirs}/incidents")).StatusCode);

        // Nhân viên ghi chú nội bộ khi chuyển trạng thái.
        (await responder.PatchAsJsonAsync($"/api/projects/{mine}/incidents/{id}/status",
            new { targetStatus = "Mitigating", note = "Nghi lỗi ở vault, chưa báo khách." },
            ApiFactory.Json)).EnsureSuccessStatusCode();

        // Khách thấy dòng lịch sử nhưng KHÔNG thấy nội dung ghi chú.
        var history = await ReadJsonAsync(await customer.GetAsync($"/api/projects/{mine}/incidents/{id}/history"));
        var row = history.EnumerateArray().Single();
        Assert.Equal("Mitigating", S(row, "toStatus"));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("note").ValueKind);

        // Nhân viên thì thấy đủ.
        var staffView = await ReadJsonAsync(await responder.GetAsync($"/api/projects/{mine}/incidents/{id}/history"));
        Assert.Contains("vault", S(staffView.EnumerateArray().Single(), "note"));
    }

    /// <summary>
    /// Đợt 7 — thanh chọn project chỉ liệt kê project người dùng với tới.
    ///
    /// Không chỉ để tránh bấm vào rồi nhận 403: **tên project là dữ liệu**. Liệt kê đủ tên mọi
    /// project là đã nói cho người dùng biết hệ thống có những gì.
    /// </summary>
    [Fact]
    public async Task Danh_sach_project_chi_chua_project_minh_voi_toi()
    {
        var admin = await _fx.AdminAsync();
        var responder = await _fx.ResponderAsync();

        var mine = await NewBareProjectAsync("ds-cua-toi");
        var hidden = await NewBareProjectAsync("ds-bi-giau");
        (await admin.PutAsync($"/api/projects/{mine}/members/responder/roles/responder", null))
            .EnsureSuccessStatusCode();

        var seen = await ReadJsonAsync(await responder.GetAsync("/api/projects"));
        var slugs = seen.EnumerateArray().Select(p => S(p, "slug")).ToList();
        Assert.Contains(mine, slugs);
        Assert.DoesNotContain(hidden, slugs);

        // Admin có quyền toàn cục nên vẫn thấy cả hai.
        var all = await ReadJsonAsync(await admin.GetAsync("/api/projects"));
        var adminSlugs = all.EnumerateArray().Select(p => S(p, "slug")).ToList();
        Assert.Contains(mine, adminSlugs);
        Assert.Contains(hidden, adminSlugs);
    }

    /// <summary>
    /// <b>Test canh gác.</b> Mọi endpoint phải tự khai thuộc nhóm nào: nằm dưới
    /// <c>api/projects/{project}</c> (kiểm quyền theo project), hoặc nằm trong danh sách toàn cục
    /// khai báo tường minh dưới đây.
    ///
    /// Không có test này thì một endpoint thuộc project mà quên đặt <c>{project}</c> vào đường dẫn
    /// sẽ **âm thầm rơi về kiểm toàn cục** — nó vẫn chạy, vẫn trả dữ liệu, chỉ là không còn cách
    /// ly. Đó là loại lỗi không ai phát hiện cho tới khi muộn.
    ///
    /// Thêm một tiền tố vào danh sách này là một quyết định có ý thức: nó nghĩa là "endpoint này
    /// cố ý xuyên project, và tự chịu trách nhiệm lọc dữ liệu theo quyền".
    /// </summary>
    [Fact]
    public void Moi_endpoint_deu_tu_khai_pham_vi()
    {
        // Cố ý toàn cục. Mỗi dòng kèm lý do vì sao nó không thể là project-scoped.
        var globalPrefixes = new[]
        {
            "api/auth",           // đăng nhập, đăng ký, đổi mật khẩu — chưa biết project nào
            "api/profiles",       // hồ sơ người dùng, không thuộc project
            "api/users",          // quản trị tài khoản toàn cục (admin)
            "api/roles",          // RBAC
            "api/permissions",    // RBAC
            "api/projects",       // danh sách project và tạo project mới
            "api/project-catalog",// khách tự chọn project: chưa vào thì chưa có quyền nào
                                  // trong đó, nên kiểm theo project sẽ chặn đúng cái thao tác
                                  // dùng để vào. Chỉ đụng dữ liệu của chính người gọi.
            "api/health",
            "api/metrics",
            "api/storage",        // khoá tệp mang slug, nhưng đường dẫn thì không
            "api/markdown",       // xem trước Markdown, không đọc dữ liệu nào
            "api/search",         // xuyên project — PHẢI tự lọc kết quả theo quyền
            "api/tickets",        // như trên
            "api/notifications",  // hộp thư cá nhân, xuyên project — PHẢI tự lọc
            "api/boards",         // board xuyên project — PHẢI tự lọc
            "api/presence",       // trạng thái của chính mình, không thuộc project nào
            "api/sla-policies",   // cấu hình toàn hệ thống
            "api/issue-types",    // như trên
        };

        var routes = typeof(Api.Authorization.Permissions).Assembly.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.GetCustomAttribute<RouteAttribute>()?.Template)
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!.ToLowerInvariant())
            .Distinct()
            .ToList();

        Assert.NotEmpty(routes);

        var unclassified = routes
            .Where(r => !r.StartsWith("api/projects/{project}", StringComparison.Ordinal))
            .Where(r => !globalPrefixes.Any(p => r.Equals(p, StringComparison.Ordinal)
                                                 || r.StartsWith(p + "/", StringComparison.Ordinal)))
            .OrderBy(x => x)
            .ToList();

        Assert.True(unclassified.Count == 0,
            "Endpoint chưa khai phạm vi (thêm {project} vào đường dẫn, hoặc khai vào danh sách toàn cục "
            + $"kèm lý do): {string.Join(", ", unclassified)}");
    }
}
