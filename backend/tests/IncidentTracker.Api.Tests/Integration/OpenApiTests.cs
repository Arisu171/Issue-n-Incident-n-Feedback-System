using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Yêu cầu kỹ thuật "OpenAPI 2 endpoint" — API phải có đặc tả OpenAPI mô tả tối thiểu hai
/// endpoint nghiệp vụ. Đó là một mức sàn, và hệ thống hiện vượt xa nó.
///
/// Cách đọc còn lại — "phơi OpenAPI tại đúng hai URL" — đã bị loại vì Swashbuckle tự sinh cả
/// bốn đường dẫn (<c>/swagger</c>, <c>index.html</c>, <c>.json</c>, <c>.yaml</c>) nên con số
/// hai không khớp, và vì nó buộc phải phơi bề mặt API ra production mà không đổi lại được gì.
/// </summary>
[Collection(ApiCollection.Name)]
public class OpenApiTests
{
    private readonly ApiFixture _fx;

    public OpenApiTests(ApiFixture fx) => _fx = fx;

    // ---------- Yêu cầu chính: đặc tả mô tả đủ endpoint ----------

    [Fact]
    public async Task Dac_ta_OpenAPI_mo_ta_toi_thieu_hai_endpoint_nghiep_vu()
    {
        var client = _fx.Factory.CreateClient();
        var spec = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");

        var paths = spec.GetProperty("paths");
        var pathCount = paths.EnumerateObject().Count();
        var operationCount = paths.EnumerateObject().Sum(p => p.Value.EnumerateObject().Count());

        Assert.True(pathCount >= 2, $"Đặc tả chỉ mô tả {pathCount} path.");
        Assert.True(operationCount >= 2, $"Đặc tả chỉ mô tả {operationCount} operation.");

        // Ghi lại mức thực tế để lần nào tụt xuống cũng thấy ngay trong log test.
        Assert.True(operationCount >= 25,
            $"Bề mặt API tụt xuống {operationCount} operation trên {pathCount} path.");
    }

    [Fact]
    public async Task Dac_ta_mo_ta_du_cac_endpoint_cot_loi_cua_nghiep_vu()
    {
        var client = _fx.Factory.CreateClient();
        var spec = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var paths = spec.GetProperty("paths");

        foreach (var required in new[]
                 {
                     // Đây là **mẫu đường dẫn** trong đặc tả, không phải URL gọi thật: `{project}`
                     // là tham số route. Điền một slug cụ thể vào đây là kiểm nhầm thứ khác.
                     "/api/auth/login",
                     "/api/projects/{project}/incidents", "/api/projects/{project}/incidents/{id}/status",
                     "/api/projects/{project}/incidents/{id}/history",
                     "/api/projects/{project}/feedbacks", "/api/projects/{project}/feedbacks/{id}/link",
                     "/api/users", "/api/roles", "/api/permissions"
                 })
        {
            Assert.True(paths.TryGetProperty(required, out _), $"Đặc tả thiếu {required}.");
        }
    }

    [Fact]
    public async Task Dac_ta_dung_dinh_dang_OpenAPI_va_dat_ten_dung()
    {
        var client = _fx.Factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var spec = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("3.", spec.GetProperty("openapi").GetString());
        Assert.Equal("Incident & Feedback Tracker API",
            spec.GetProperty("info").GetProperty("title").GetString());
    }

    /// <summary>Không khai báo bearer thì người dùng Swagger UI không dán được JWT vào thử.</summary>
    [Fact]
    public async Task Dac_ta_khai_bao_bearer_de_thu_duoc_endpoint_can_quyen()
    {
        var client = _fx.Factory.CreateClient();
        var spec = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");

        var scheme = spec.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");

        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
    }

    /// <summary>NFR-SEC-02 — đặc tả là tài liệu chia sẻ được, không được mang theo bí mật.</summary>
    [Fact]
    public async Task Dac_ta_khong_chua_bi_mat_nao()
    {
        var client = _fx.Factory.CreateClient();
        var raw = await client.GetStringAsync("/swagger/v1/swagger.json");

        Assert.DoesNotContain(ApiFactory.SigningKey, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiFixture.AdminPassword, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", raw, StringComparison.Ordinal); // chuỗi kết nối
    }

    [Fact]
    public async Task Swagger_UI_hien_thi_duoc_khi_bat()
    {
        var client = _fx.Factory.CreateClient();
        var response = await client.GetAsync("/swagger/index.html");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("swagger-ui", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    // ---------- Tư thế bảo mật của công tắc ----------

    /// <summary>
    /// Mặc định an toàn: ngoài Development thì không phơi gì cả. Đặc tả công khai giúp người lạ
    /// liệt kê toàn bộ bề mặt API, nên đó phải là hành vi chọn mới có, không phải mặc định.
    /// </summary>
    [Fact]
    public async Task Mac_dinh_ngoai_Development_thi_khong_phoi_Swagger()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Swagger:Enabled", string.Empty));

        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/swagger/index.html")).StatusCode);

        // Nhưng API vẫn chạy bình thường — tắt tài liệu không đụng gì tới nghiệp vụ.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }

    /// <summary>Bật tường minh vẫn dùng được, phục vụ bản triển khai nội bộ hoặc buổi demo.</summary>
    [Fact]
    public async Task Bat_tuong_minh_thi_phoi_duoc_o_moi_truong_bat_ky()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Swagger:Enabled", "true"));

        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/index.html")).StatusCode);
    }

    /// <summary>Giá trị sai định dạng không được làm sập app mà phải rơi về mặc định an toàn.</summary>
    [Fact]
    public async Task Gia_tri_cau_hinh_sai_dinh_dang_roi_ve_mac_dinh_thay_vi_lam_sap_app()
    {
        using var factory = _fx.Factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Swagger:Enabled", "khong-phai-bool"));

        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }
}
