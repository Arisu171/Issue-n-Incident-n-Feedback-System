using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>TC-001 … TC-005 — Identity, RBAC và ma trận 401/403.</summary>
[Collection(ApiCollection.Name)]
public class RbacAndAuthTests
{
    private readonly ApiFixture _fx;

    public RbacAndAuthTests(ApiFixture fx) => _fx = fx;

    // ---------- TC-001 ----------

    [Fact]
    public async Task TC001_tao_user_luu_hash_va_khong_tra_ve_hash()
    {
        var admin = await _fx.AdminAsync();
        var email = $"tc001-{Guid.NewGuid():N}@test.local";

        var response = await admin.PostAsJsonAsync("/api/users",
            new { email, displayName = "TC001", password = "Secret#12345", roles = new[] { "customer" } },
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret#12345", body, StringComparison.Ordinal);

        await using var db = _fx.Factory.CreateDbContext();
        var stored = await db.Users.SingleAsync(u => u.Email == email);
        Assert.NotEqual("Secret#12345", stored.PasswordHash);
        Assert.True(stored.PasswordHash.Length > 20);
    }

    [Fact]
    public async Task TC001_email_trung_tra_409()
    {
        var admin = await _fx.AdminAsync();
        var email = $"tc001dup-{Guid.NewGuid():N}@test.local";
        var payload = new { email, displayName = "Trùng", password = "Secret#12345", roles = Array.Empty<string>() };

        Assert.Equal(HttpStatusCode.Created,
            (await admin.PostAsJsonAsync("/api/users", payload, ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await admin.PostAsJsonAsync("/api/users", payload, ApiFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task BR01_email_duoc_chuan_hoa_lowercase()
    {
        var admin = await _fx.AdminAsync();
        var raw = $"  TC001-MIXED-{Guid.NewGuid():N}@Test.Local  ";

        var response = await admin.PostAsJsonAsync("/api/users",
            new { email = raw, displayName = "Mixed case", password = "Secret#12345", roles = Array.Empty<string>() },
            ApiFactory.Json);
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<UserDto>(ApiFactory.Json);
        Assert.Equal(raw.Trim().ToLowerInvariant(), created!.Email);
    }

    // ---------- TC-002 ----------

    [Fact]
    public async Task TC002_gan_role_idempotent_va_khong_tao_cap_trung()
    {
        var admin = await _fx.AdminAsync();

        var user = await CreateUserAsync(admin, "TC002");
        var roles = await admin.GetFromJsonAsync<List<ApiFixture.RoleDto>>("/api/roles", ApiFactory.Json);
        var role = roles!.Single(r => r.Name == "customer");

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsync($"/api/users/{user.Id}/roles/{role.Id}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PutAsync($"/api/users/{user.Id}/roles/{role.Id}", null)).StatusCode);

        await using var db = _fx.Factory.CreateDbContext();
        Assert.Equal(1, await db.UserRoles.CountAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id));
    }

    [Fact]
    public async Task TC002_gan_role_khong_ton_tai_tra_404()
    {
        var admin = await _fx.AdminAsync();
        var user = await CreateUserAsync(admin, "TC002-404");

        var response = await admin.PutAsync($"/api/users/{user.Id}/roles/{Guid.NewGuid()}", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- TC-003 ----------

    [Fact]
    public async Task TC003_dang_nhap_dung_tra_token_kem_permission()
    {
        var client = _fx.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = ApiFixture.SupportEmail, password = ApiFixture.Password }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<LoginDto>(ApiFactory.Json);
        Assert.False(string.IsNullOrWhiteSpace(payload!.AccessToken));
        Assert.Contains("incident.create", payload.Permissions);
        Assert.DoesNotContain("incident.resolve", payload.Permissions);
    }

    [Fact]
    public async Task TC003_sai_mat_khau_tra_401_khong_lo_thong_tin_user()
    {
        var client = _fx.Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = ApiFixture.SupportEmail, password = "sai-mat-khau" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(ApiFixture.Password, body);
    }

    /// <summary>FR-008 / BR-04 — user inactive không nhận token mới.</summary>
    [Fact]
    public async Task TC003_user_inactive_khong_nhan_duoc_token()
    {
        var admin = await _fx.AdminAsync();
        var user = await CreateUserAsync(admin, "TC003-inactive");

        var deactivate = await admin.PatchAsJsonAsync($"/api/users/{user.Id}",
            new { isActive = false }, ApiFactory.Json);
        deactivate.EnsureSuccessStatusCode();

        var client = _fx.Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = user.Email, password = "Secret#12345" }, ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    // ---------- TC-004 / TC-005 ----------

    [Fact]
    public async Task TC004_co_quyen_thi_goi_duoc_protected_api()
    {
        var admin = await _fx.AdminAsync();
        var response = await admin.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("passwordHash", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TC005_thieu_token_tra_401()
    {
        var client = _fx.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task TC005_token_hong_tra_401()
    {
        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "khong-phai-jwt");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task TC005_token_het_han_tra_401()
    {
        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenBuilder.Expired());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task TC005_sai_issuer_tra_401()
    {
        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenBuilder.WrongIssuer());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task TC005_thieu_permission_tra_403()
    {
        var readOnly = await _fx.ReadOnlyAsync();
        var response = await readOnly.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Chu_ky_bi_gia_mao_tra_401()
    {
        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenBuilder.WrongSigningKey());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Moi_response_deu_co_correlation_id()
    {
        var client = _fx.Factory.CreateClient();
        var response = await client.GetAsync("/api/health");

        Assert.True(response.Headers.Contains("X-Correlation-ID"));
    }

    // ---------- Helper ----------

    private static async Task<UserDto> CreateUserAsync(HttpClient admin, string prefix)
    {
        var response = await admin.PostAsJsonAsync("/api/users",
            new
            {
                email = $"{prefix.ToLowerInvariant()}-{Guid.NewGuid():N}@test.local",
                displayName = prefix,
                password = "Secret#12345",
                roles = Array.Empty<string>()
            }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserDto>(ApiFactory.Json))!;
    }

    public sealed record UserDto(Guid Id, string Email, string DisplayName, bool IsActive);
    public sealed record LoginDto(string AccessToken, Guid UserId, string[] Roles, string[] Permissions);
}
