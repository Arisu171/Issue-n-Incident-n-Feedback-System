using System.Net;
using System.Net.Http.Json;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Vai trò <c>system</c> (rank 120) và ranh giới của nó với <c>admin</c> (rank 100).
///
/// <b>Vì sao nhóm test này tồn tại.</b> Cấu hình nền — ngưỡng SLA và loại vấn đề — là thứ hiếm
/// khi đụng tới nhưng đụng sai thì ảnh hưởng toàn hệ thống, nên nó được tách khỏi công việc quản
/// trị hằng ngày. Ranh giới ấy chỉ nằm trong hai chỗ: danh mục permission, và ràng buộc rank
/// trong RBAC. Không có test thì trả <c>sla.manage</c> về cho <c>admin</c> vẫn xanh toàn bộ và
/// không ai biết — chính là lỗ hổng nhóm này bịt lại.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SystemRoleTests
{
    private readonly ApiFixture _fx;

    public SystemRoleTests(ApiFixture fx) => _fx = fx;

    /// <summary>Hai tài khoản tách bạch: admin KHÔNG được mang vai trò system.</summary>
    [Fact]
    public async Task Admin_khong_mang_vai_tro_system()
    {
        var admin = await _fx.AdminAsync();
        var me = await ReadJsonAsync(await admin.GetAsync("/api/auth/me"));
        var roles = me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();

        Assert.Contains("admin", roles);
        Assert.DoesNotContain("system", roles);
    }

    [Fact]
    public async Task Tai_khoan_system_chi_mang_vai_tro_system()
    {
        var system = await _fx.SystemAsync();
        var me = await ReadJsonAsync(await system.GetAsync("/api/auth/me"));
        var roles = me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();

        Assert.Equal(new[] { "system" }, roles);
    }

    /// <summary>
    /// Ngưỡng SLA: admin bị chặn, và câu trả lời nói rõ quyền còn thiếu thay vì 403 trống.
    /// </summary>
    [Fact]
    public async Task Admin_khong_sua_duoc_nguong_sla()
    {
        var admin = await _fx.AdminAsync();
        var response = await admin.PutAsJsonAsync("/api/sla-policies/P2",
            new { responseTimeMinutes = 30, resolutionTimeMinutes = 300, escalateAfterMinutes = 60, isActive = true },
            ApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("sla.manage", S(body, "requiredPermission"));
    }

    /// <summary>Đối chứng: cùng lời gọi, chỉ khác tài khoản.</summary>
    [Fact]
    public async Task System_sua_duoc_nguong_sla()
    {
        var system = await _fx.SystemAsync();
        var response = await system.PutAsJsonAsync("/api/sla-policies/P3",
            new { responseTimeMinutes = 1440, resolutionTimeMinutes = 10080, escalateAfterMinutes = 1440, isActive = true },
            ApiFactory.Json);

        response.EnsureSuccessStatusCode();
        var body = await ReadJsonAsync(response);
        Assert.Equal(1440, body.GetProperty("responseTimeMinutes").GetInt32());
    }

    /// <summary>
    /// Chốt chặn thật của việc tách tài khoản.
    ///
    /// Nếu admin tự gán được vai trò <c>system</c> thì mọi thứ ở trên chỉ là hình thức — một
    /// lời gọi API là admin lấy lại toàn bộ quyền cấu hình nền. Ràng buộc rank trong RBAC
    /// ("role ngang hoặc cao hơn cấp của bạn") là thứ khiến ranh giới có hiệu lực.
    /// </summary>
    [Fact]
    public async Task Admin_khong_tu_gan_duoc_vai_tro_system()
    {
        var admin = await _fx.AdminAsync();

        var me = await ReadJsonAsync(await admin.GetAsync("/api/auth/me"));
        var myId = me.GetProperty("id").GetGuid();

        var roles = await ReadJsonAsync(await admin.GetAsync("/api/roles"));
        var systemRoleId = roles.EnumerateArray()
            .First(r => S(r, "name") == "system")
            .GetProperty("id").GetGuid();

        var response = await admin.PutAsync($"/api/users/{myId}/roles/{systemRoleId}", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Và đúng là vẫn chưa có: 403 phải là chặn thật, không phải chặn rồi vẫn ghi.
        var after = await ReadJsonAsync(await admin.GetAsync("/api/auth/me"));
        Assert.DoesNotContain("system",
            after.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }
}
