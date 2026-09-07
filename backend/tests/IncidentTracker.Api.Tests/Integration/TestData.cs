using System.Net.Http.Json;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Collection fixture — dựng một database sạch cho cả bộ integration test và tạo sẵn
/// bốn tài khoản đại diện cho ma trận quyền của bảng 9.2.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    public ApiFactory Factory { get; } = new();

    public const string AdminEmail = "admin@test.local";
    public const string AdminPassword = "Admin#12345";

    /// <summary>Tài khoản mang MỘT vai trò `system`, không kèm `admin`.</summary>
    public const string SystemEmail = "system@test.local";
    public const string SystemPassword = "System#12345";

    public const string SupportEmail = "support@test.local";
    public const string ResponderEmail = "responder@test.local";
    /// <summary>
    /// Tài khoản chỉ đọc. Trước đây dùng vai trò dựng sẵn <c>viewer</c>; vai trò đó đã bị bỏ vì
    /// nó chỉ khác <c>customer</c> ở chỗ xem được mọi sự cố, mà quyền đó sắp cấp cho customer.
    ///
    /// Bộ quyền vẫn giữ **nguyên như viewer cũ**, chỉ là giờ do fixture tự dựng: đổi tài khoản
    /// này sang <c>customer</c> sẽ làm hỏng chính những gì các test đang chứng minh — customer có
    /// <c>feedback.read</c> và <c>incident.comment</c> nên hai test "không đọc được feedback" và
    /// "đọc được nhưng không viết được" sẽ xanh vì lý do sai.
    ///
    /// Nó cũng là bằng chứng vai trò tự tạo vẫn chạy đúng sau khi gỡ một vai trò dựng sẵn.
    /// </summary>
    public const string ReadOnlyEmail = "readonly@test.local";

    /// <summary>ACT-BIZ-03 — khách hàng tự gửi ticket, chỉ thấy đúng phần của mình.</summary>
    public const string CustomerEmail = "customer@test.local";

    /// <summary>Khách hàng thứ hai: cần hai người mới chứng minh được BOLA đã bị chặn.</summary>
    public const string OtherCustomerEmail = "customer2@test.local";

    /// <summary>
    /// Role riêng chỉ có <c>incident.update_status</c> mà KHÔNG có <c>incident.resolve</c>.
    /// Bảng 9.2 cho responder cả hai quyền nên nhánh 403 của US-BIZ-03/AC-03 sẽ không bao giờ
    /// xảy ra với role mặc định — phải dựng role này thì test mới phủ được (rủi ro đã nêu
    /// trong báo cáo thẩm định).
    /// </summary>
    public const string MitigatorEmail = "mitigator@test.local";

    public const string Password = "Test#12345";

    public async Task InitializeAsync()
    {
        await ((IAsyncLifetime)Factory).InitializeAsync();
        _ = Factory.Services; // Kích hoạt host: chạy migration và seed permission/role.

        var admin = await Factory.CreateClientAsAsync(AdminEmail, AdminPassword);

        await CreateUserAsync(admin, SupportEmail, "Nhân viên hỗ trợ", "support");
        await CreateUserAsync(admin, ResponderEmail, "Kỹ thuật viên", "responder");
        await CreateUserAsync(admin, CustomerEmail, "Khách hàng A", "customer");
        await CreateUserAsync(admin, OtherCustomerEmail, "Khách hàng B", "customer");

        await CreateRoleAsync(admin, "readonly-test", "Chỉ tra cứu, không ghi được gì",
            "incident.read", "incident.read.all", "ticket.read");
        await CreateUserAsync(admin, ReadOnlyEmail, "Người xem", "readonly-test");

        // Role "mitigator": chỉ update_status, không resolve.
        var roles = await admin.GetFromJsonAsync<List<RoleDto>>("/api/roles", ApiFactory.Json);
        if (roles!.All(r => r.Name != "mitigator"))
        {
            var created = await admin.PostAsJsonAsync("/api/roles",
                new { name = "mitigator", description = "Chỉ được chuyển sang Mitigating" }, ApiFactory.Json);
            created.EnsureSuccessStatusCode();
            var role = await created.Content.ReadFromJsonAsync<RoleDto>(ApiFactory.Json);

            var permissions = await admin.GetFromJsonAsync<List<PermissionDto>>(
                "/api/permissions", ApiFactory.Json);

            foreach (var code in new[] { "incident.read", "incident.update_status" })
            {
                var permission = permissions!.Single(p => p.Code == code);
                var assign = await admin.PutAsync(
                    $"/api/roles/{role!.Id}/permissions/{permission.Id}", null);
                assign.EnsureSuccessStatusCode();
            }
        }

        await CreateUserAsync(admin, MitigatorEmail, "Kỹ thuật viên hạn chế", "mitigator");

        // Quyền giờ gắn theo project: một tài khoản có vai trò `support` mà không được cấp vào
        // project nào thì không làm gì được ở đâu. Project mặc định `support` do TicketSeeder tạo
        // nên phải cấp tay ở đây.
        await GrantProjectAccessAsync(admin, TicketApi.Project);
    }

    /// <summary>
    /// Cấp cho dàn tài khoản chuẩn quyền trên một project.
    ///
    /// Gọi ngay sau khi test tạo project mới. Không có bước này thì mọi endpoint dưới
    /// <c>api/projects/{slug}/…</c> trả 403 — đúng như thiết kế, nhưng test thì cần dựng sẵn bối
    /// cảnh "những người này làm ở project này".
    /// </summary>
    public async Task GrantProjectAccessAsync(HttpClient admin, string slug)
    {
        foreach (var (login, role) in new[]
                 {
                     ("support", "support"),
                     ("responder", "responder"),
                     ("mitigator", "mitigator"),
                     ("readonly", "readonly-test"),
                 })
        {
            var response = await admin.PutAsync($"/api/projects/{slug}/members/{login}/roles/{role}", null);
            response.EnsureSuccessStatusCode();
        }

        // Khách hàng cấp theo vai trò, không theo từng người — đúng mô hình đã chốt.
        (await admin.PutAsync($"/api/projects/{slug}/role-access/customer", null)).EnsureSuccessStatusCode();

        // …và chính khách hàng phải tự nhận project thì mới vào được: cấp cho vai trò mới là mở
        // danh mục, quyền thật là giao của hai vế. Test cần bối cảnh "khách này đang dùng dịch
        // vụ của project này", nên dựng nốt vế thứ hai ở đây — bằng đúng endpoint mà giao diện
        // dùng, không phải bằng cách ghi thẳng vào bảng.
        await JoinProjectAsync(CustomerEmail, slug);
        await JoinProjectAsync(OtherCustomerEmail, slug);
    }

    /// <summary>Một khách hàng tự nhận project, qua đúng đường mà tab Projects gọi.</summary>
    public async Task JoinProjectAsync(string email, string slug)
    {
        var client = await Factory.CreateClientAsAsync(email, Password);
        (await client.PutAsync($"/api/project-catalog/{slug}", null)).EnsureSuccessStatusCode();
    }

    /// <summary>Tạo role kèm permission, bỏ qua nếu đã có từ lần chạy trước trong cùng fixture.</summary>
    private static async Task CreateRoleAsync(HttpClient admin, string name, string description, params string[] codes)
    {
        var roles = await admin.GetFromJsonAsync<List<RoleDto>>("/api/roles", ApiFactory.Json);
        if (roles!.Any(r => r.Name == name)) return;

        var created = await admin.PostAsJsonAsync("/api/roles", new { name, description }, ApiFactory.Json);
        created.EnsureSuccessStatusCode();
        var role = await created.Content.ReadFromJsonAsync<RoleDto>(ApiFactory.Json);

        var permissions = await admin.GetFromJsonAsync<List<PermissionDto>>("/api/permissions", ApiFactory.Json);
        foreach (var code in codes)
        {
            var permission = permissions!.Single(p => p.Code == code);
            (await admin.PutAsync($"/api/roles/{role!.Id}/permissions/{permission.Id}", null)).EnsureSuccessStatusCode();
        }
    }

    private static async Task CreateUserAsync(HttpClient admin, string email, string name, params string[] roles)
    {
        var response = await admin.PostAsJsonAsync("/api/users",
            new { email, displayName = name, password = Password, roles }, ApiFactory.Json);

        // 409 nghĩa là user đã tồn tại từ lần chạy trước trong cùng fixture — chấp nhận.
        if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public Task<HttpClient> AdminAsync() => Factory.CreateClientAsAsync(AdminEmail, AdminPassword);
    public Task<HttpClient> SystemAsync() => Factory.CreateClientAsAsync(SystemEmail, SystemPassword);
    public Task<HttpClient> SupportAsync() => Factory.CreateClientAsAsync(SupportEmail, Password);
    public Task<HttpClient> ResponderAsync() => Factory.CreateClientAsAsync(ResponderEmail, Password);
    public Task<HttpClient> ReadOnlyAsync() => Factory.CreateClientAsAsync(ReadOnlyEmail, Password);
    public Task<HttpClient> CustomerAsync() => Factory.CreateClientAsAsync(CustomerEmail, Password);
    public Task<HttpClient> OtherCustomerAsync() => Factory.CreateClientAsAsync(OtherCustomerEmail, Password);
    public Task<HttpClient> MitigatorAsync() => Factory.CreateClientAsAsync(MitigatorEmail, Password);

    public Task DisposeAsync() => ((IAsyncLifetime)Factory).DisposeAsync();

    public sealed record RoleDto(Guid Id, string Name, string? Description, string[] Permissions);
    public sealed record PermissionDto(Guid Id, string Code, string? Description);
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
