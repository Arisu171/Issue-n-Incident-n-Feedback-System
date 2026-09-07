using System.Net.Http.Json;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Ô tìm kiếm trên từng màn hình danh sách.
///
/// Trang Search riêng đã bỏ; mỗi danh sách tự có ô tìm của nó. Điều phải khoá lại không phải là
/// "có lọc" mà là <b>lọc ở đâu</b>: chữ tìm phải vào thẳng câu truy vấn, vì lọc sau khi lấy về
/// thì con số tổng của phân trang vẫn đếm cả phần không khớp và trang 2 sẽ bỏ sót.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ListSearchTests
{
    private readonly ApiFixture _fx;

    public ListSearchTests(ApiFixture fx) => _fx = fx;

    private async Task<string> NewProjectAsync(string prefix)
    {
        var admin = await _fx.AdminAsync();
        var slug = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.PostAsJsonAsync("/api/projects", new { slug, name = "Project " + prefix }, ApiFactory.Json))
            .EnsureSuccessStatusCode();
        return slug;
    }

    [Fact]
    public async Task Tim_su_co_theo_tieu_de_va_mo_ta()
    {
        var admin = await _fx.AdminAsync();
        var slug = await NewProjectAsync("tim-sc");

        foreach (var (title, description) in new[]
                 {
                     ("Vault hết hạn chứng chỉ", "Chi tiết trong runbook"),
                     ("Cổng thanh toán chậm", "Nghi do vault quá tải"),
                     ("Đăng nhập lỗi 500", "Không liên quan"),
                 })
        {
            (await admin.PostAsJsonAsync($"/api/projects/{slug}/incidents",
                new { title, description, severity = "Medium" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        }

        // Khớp ở tiêu đề của cái thứ nhất, ở mô tả của cái thứ hai.
        var found = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/incidents?q=vault&pageSize=100"));
        var titles = found.GetProperty("items").EnumerateArray().Select(x => S(x, "title")).ToList();
        Assert.Equal(2, titles.Count);
        Assert.DoesNotContain(titles, t => t.Contains("Đăng nhập", StringComparison.Ordinal));

        // Và tổng số của phân trang là tổng **sau khi lọc** — dấu hiệu chữ tìm đã vào câu truy vấn
        // chứ không phải lọc trên mảng đã lấy về.
        Assert.Equal(2, found.GetProperty("totalCount").GetInt32());

        // Không phân biệt hoa thường.
        var upper = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/incidents?q=VAULT&pageSize=100"));
        Assert.Equal(2, upper.GetProperty("totalCount").GetInt32());

        // Bỏ trống thì không lọc gì.
        var all = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/incidents?pageSize=100"));
        Assert.Equal(3, all.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Tim_phan_hoi_theo_noi_dung_va_email()
    {
        var admin = await _fx.AdminAsync();
        var slug = await NewProjectAsync("tim-fb");

        (await admin.PostAsJsonAsync($"/api/projects/{slug}/feedbacks",
            new { channel = "Email", content = "Ứng dụng báo lỗi khi hoàn tiền.", customerEmail = "an@khach.local" },
            ApiFactory.Json)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"/api/projects/{slug}/feedbacks",
            new { channel = "Hotline", content = "Nhân viên hỗ trợ rất nhiệt tình.", customerEmail = "binh@khach.local" },
            ApiFactory.Json)).EnsureSuccessStatusCode();

        var byContent = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/feedbacks?q=hoàn tiền&pageSize=100"));
        Assert.Equal(1, byContent.GetProperty("totalCount").GetInt32());

        var byEmail = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/feedbacks?q=binh@&pageSize=100"));
        Assert.Equal(1, byEmail.GetProperty("totalCount").GetInt32());
        Assert.Contains("nhiệt tình", S(byEmail.GetProperty("items").EnumerateArray().Single(), "content"));

        var none = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/feedbacks?q=khongcogi&pageSize=100"));
        Assert.Equal(0, none.GetProperty("totalCount").GetInt32());
    }

    /// <summary>
    /// Chữ tìm <b>không</b> được phép nới ranh giới đọc: nó là bộ lọc chồng lên phạm vi đã cắt,
    /// không phải một đường vòng để đọc dữ liệu của project khác.
    /// </summary>
    [Fact]
    public async Task Chu_tim_khong_vuot_qua_ranh_gioi_project()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();

        var mine = await NewProjectAsync("tim-cua-toi");
        var theirs = await NewProjectAsync("tim-cua-nguoi");
        (await admin.PutAsync($"/api/projects/{mine}/members/support/roles/support", null)).EnsureSuccessStatusCode();

        var token = "zztim" + Guid.NewGuid().ToString("N")[..6];
        foreach (var slug in new[] { mine, theirs })
        {
            (await admin.PostAsJsonAsync($"/api/projects/{slug}/incidents",
                new { title = $"{token} ở {slug}", severity = "Low" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        }

        var seen = await ReadJsonAsync(await support.GetAsync($"/api/projects/{mine}/incidents?q={token}&pageSize=100"));
        Assert.Equal(1, seen.GetProperty("totalCount").GetInt32());

        Assert.Equal(System.Net.HttpStatusCode.Forbidden,
            (await support.GetAsync($"/api/projects/{theirs}/incidents?q={token}")).StatusCode);
    }
}
