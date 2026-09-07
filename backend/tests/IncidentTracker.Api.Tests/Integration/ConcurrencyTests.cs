using System.Net;
using System.Net.Http.Json;
using IncidentTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using static IncidentTracker.Api.Tests.Integration.IncidentLifecycleTests;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Bằng chứng cho <c>SELECT ... FOR UPDATE</c> ở mục 6.5 và cho câu hỏi phản biện của README:
/// <i>"Điều gì xảy ra nếu hai Kỹ thuật viên cùng lúc bấm chuyển trạng thái cho cùng một sự cố?"</i>
///
/// Nếu không khóa hàng, cả hai request cùng đọc <c>Investigating</c>, cùng đi qua kiểm tra
/// transition và cùng ghi lịch sử — sinh ra hai dòng cho một lần chuyển, phá vỡ BR-BIZ-06 và
/// làm số liệu đo thời gian xử lý sai. Các test dưới đây chứng minh điều đó không xảy ra.
/// </summary>
[Collection(ApiCollection.Name)]
public class ConcurrencyTests
{
    private const int Racers = 8;

    private readonly ApiFixture _fx;

    public ConcurrencyTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Tam_request_chuyen_trang_thai_cung_luc_chi_mot_cai_thanh_cong()
    {
        var support = await _fx.SupportAsync();
        var incident = await CreateIncidentAsync(support, "Đua chuyển trạng thái đồng thời");

        // Mỗi "kỹ thuật viên" là một HttpClient riêng để không dùng chung connection pool.
        var clients = await Task.WhenAll(
            Enumerable.Range(0, Racers).Select(_ => _fx.ResponderAsync()));

        // Chặn tất cả ở cùng một vạch xuất phát rồi thả ra cùng lúc.
        var gate = new TaskCompletionSource();

        var attempts = clients.Select(async client =>
        {
            await gate.Task;
            return await client.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
                new { targetStatus = "Mitigating" }, ApiFactory.Json);
        }).ToList();

        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var conflicted = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, succeeded);
        Assert.Equal(Racers - 1, conflicted);

        // Không có mã trạng thái nào khác lọt ra — đặc biệt không được có 500.
        Assert.All(responses, r =>
            Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                $"Nhận được {(int)r.StatusCode} ngoài dự kiến."));

        await using var db = _fx.Factory.CreateDbContext();
        var stored = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == incident.Id);
        Assert.Equal(IncidentStatus.Mitigating, stored.Status);

        // BR-BIZ-06: đúng một dòng lịch sử cho một lần chuyển, dù có tám request tranh nhau.
        Assert.Equal(1, await db.IncidentStatusHistory.CountAsync(h => h.IncidentId == incident.Id));
    }

    /// <summary>
    /// Trường hợp nguy hiểm hơn: nhiều người cùng bấm <c>Resolved</c>. Nếu lọt hai lần,
    /// <c>resolved_at</c> sẽ bị ghi đè và số đo thời gian khắc phục mất ý nghĩa.
    /// </summary>
    [Fact]
    public async Task Nhieu_request_dong_su_co_cung_luc_chi_mot_cai_ghi_resolvedAt()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();

        var incident = await CreateIncidentAsync(support, "Đua đóng sự cố đồng thời");
        await TransitionAsync(responder, incident.Id, "Mitigating");

        var clients = await Task.WhenAll(
            Enumerable.Range(0, Racers).Select(_ => _fx.ResponderAsync()));

        var gate = new TaskCompletionSource();
        var attempts = clients.Select(async client =>
        {
            await gate.Task;
            return await client.PatchAsJsonAsync($"/api/projects/support/incidents/{incident.Id}/status",
                new { targetStatus = "Resolved", note = "Đóng đồng thời" }, ApiFactory.Json);
        }).ToList();

        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

        await using var db = _fx.Factory.CreateDbContext();
        var stored = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == incident.Id);

        Assert.Equal(IncidentStatus.Resolved, stored.Status);
        Assert.NotNull(stored.ResolvedAt);
        Assert.Equal(2, await db.IncidentStatusHistory.CountAsync(h => h.IncidentId == incident.Id));
    }

    /// <summary>
    /// Gán người xử lý là thao tác idempotent nên nhiều request song song đều phải trả 204,
    /// và tuyệt đối không được sinh bản ghi trùng hay lỗi khóa.
    /// </summary>
    [Fact]
    public async Task Gan_nguoi_xu_ly_dong_thoi_van_idempotent()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var me = await responder.GetFromJsonAsync<MeDto>("/api/auth/me", ApiFactory.Json);
        var incident = await CreateIncidentAsync(support, "Đua gán người xử lý");

        var clients = await Task.WhenAll(
            Enumerable.Range(0, Racers).Select(_ => _fx.AdminAsync()));

        var gate = new TaskCompletionSource();
        var attempts = clients.Select(async client =>
        {
            await gate.Task;
            return await client.PutAsync($"/api/projects/support/incidents/{incident.Id}/assignee/{me!.Id}", null);
        }).ToList();

        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));

        await using var db = _fx.Factory.CreateDbContext();
        var stored = await db.Incidents.AsNoTracking().SingleAsync(i => i.Id == incident.Id);
        Assert.Equal(me!.Id, stored.AssigneeId);
    }

    /// <summary>
    /// Hai request tạo user cùng email cùng lúc: unique index của PostgreSQL phải chặn cái thứ hai
    /// và API phải dịch thành 409 chứ không để lộ lỗi DB thành 500 (BR-01).
    /// </summary>
    [Fact]
    public async Task Tao_user_trung_email_dong_thoi_tra_409_khong_phai_500()
    {
        var email = $"race-{Guid.NewGuid():N}@test.local";
        var clients = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => _fx.AdminAsync()));

        var gate = new TaskCompletionSource();
        var attempts = clients.Select(async client =>
        {
            await gate.Task;
            return await client.PostAsJsonAsync("/api/users",
                new { email, displayName = "Đua tạo user", password = "Secret#12345", roles = Array.Empty<string>() },
                ApiFactory.Json);
        }).ToList();

        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.DoesNotContain(responses, r => r.StatusCode == HttpStatusCode.InternalServerError);

        await using var db = _fx.Factory.CreateDbContext();
        Assert.Equal(1, await db.Users.CountAsync(u => u.Email == email));
    }
}
