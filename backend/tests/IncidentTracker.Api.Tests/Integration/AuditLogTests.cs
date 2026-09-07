using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using static IncidentTracker.Api.Tests.Integration.IncidentLifecycleTests;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// FR-011, NFR-AUD-01 và NFR-SEC-02 — kiểm chứng nội dung log thay vì đọc bằng mắt.
///
/// Các test này chạy tuần tự trong cùng collection nên log của bài khác có thể xen vào;
/// vì vậy mỗi bài lọc theo id của đối tượng mình vừa tạo chứ không dựa vào thứ tự.
/// </summary>
[Collection(ApiCollection.Name)]
public class AuditLogTests
{
    private readonly ApiFixture _fx;

    public AuditLogTests(ApiFixture fx) => _fx = fx;

    /// <summary>NFR-AUD-01 — sự kiện quản trị phải có actor, target, action và result.</summary>
    [Fact]
    public async Task Su_kien_quan_tri_ghi_du_actor_target_action_result()
    {
        var admin = await _fx.AdminAsync();
        var email = $"audit-{Guid.NewGuid():N}@test.local";

        var response = await admin.PostAsJsonAsync("/api/users",
            new { email, displayName = "Audit", password = "Secret#12345", roles = Array.Empty<string>() },
            ApiFactory.Json);
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<RbacAndAuthTests.UserDto>(ApiFactory.Json);

        var entry = _fx.Factory.Logs.AuditEntries
            .Single(e => e.Message.Contains("rbac.user.create")
                         && e.Message.Contains(created!.Id.ToString()));

        Assert.Contains("actor=", entry.Message);
        Assert.Contains("target=", entry.Message);
        Assert.Contains("result=success", entry.Message);
        Assert.Equal(LogLevel.Information, entry.Level);
    }

    /// <summary>Mỗi lần chuyển trạng thái để lại một dòng audit có from, to và actor.</summary>
    [Fact]
    public async Task Chuyen_trang_thai_ghi_audit_kem_from_va_to()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var me = await responder.GetFromJsonAsync<MeDto>("/api/auth/me", ApiFactory.Json);

        var incident = await CreateIncidentAsync(support, "Kiểm chứng audit chuyển trạng thái");
        await TransitionAsync(responder, incident.Id, "Mitigating");

        var entry = _fx.Factory.Logs.AuditEntries
            .Single(e => e.Message.Contains("incident.status")
                         && e.Message.Contains(incident.Id.ToString()));

        Assert.Contains($"actor={me!.Id}", entry.Message);
        Assert.Contains("from=Investigating", entry.Message);
        Assert.Contains("to=Mitigating", entry.Message);
        Assert.Contains("result=success", entry.Message);
    }

    /// <summary>FR-011 — đăng nhập thất bại phải được ghi log.</summary>
    [Fact]
    public async Task Dang_nhap_that_bai_duoc_ghi_log_canh_bao()
    {
        var client = _fx.Factory.CreateClient();

        await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = ApiFixture.SupportEmail, password = "mat-khau-hoan-toan-sai" },
            ApiFactory.Json);

        Assert.Contains(_fx.Factory.Logs.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("Đăng nhập thất bại"));
    }

    /// <summary>
    /// NFR-SEC-02 — mật khẩu, hash và JWT không bao giờ được xuất hiện trong log.
    /// Đây là ràng buộc dễ vi phạm nhất khi ai đó thêm một dòng log gỡ lỗi.
    /// </summary>
    [Fact]
    public async Task Log_khong_chua_mat_khau_hash_hay_token()
    {
        var admin = await _fx.AdminAsync();
        var password = "Sieu#MatKhau#12345";

        await admin.PostAsJsonAsync("/api/users",
            new
            {
                email = $"secret-{Guid.NewGuid():N}@test.local",
                displayName = "Kiểm tra rò rỉ",
                password,
                roles = Array.Empty<string>()
            }, ApiFactory.Json);

        var client = _fx.Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { identifier = ApiFixture.SupportEmail, password = ApiFixture.Password }, ApiFactory.Json);
        var token = (await login.Content.ReadFromJsonAsync<RbacAndAuthTests.LoginDto>(ApiFactory.Json))!.AccessToken;

        foreach (var entry in _fx.Factory.Logs.Entries)
        {
            Assert.DoesNotContain(password, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(ApiFixture.Password, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(token, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("AQAAAA", entry.Message, StringComparison.Ordinal); // tiền tố hash của PasswordHasher
            Assert.DoesNotContain(ApiFactory.SigningKey, entry.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// PII của khách hàng (mục 6.7) — nội dung phản hồi và email liên hệ không được vào log,
    /// dù chính bản ghi đó vừa được tạo qua API.
    /// </summary>
    [Fact]
    public async Task Log_khong_chua_PII_cua_khach_hang()
    {
        var support = await _fx.SupportAsync();
        var customerEmail = $"khach-{Guid.NewGuid():N}@example.com";
        var content = "Nội dung phản hồi nhạy cảm không được phép xuất hiện trong log hệ thống.";

        var response = await support.PostAsJsonAsync("/api/projects/support/feedbacks",
            new { channel = "Hotline", customerEmail, content }, ApiFactory.Json);
        response.EnsureSuccessStatusCode();

        foreach (var entry in _fx.Factory.Logs.Entries)
        {
            Assert.DoesNotContain(customerEmail, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(content, entry.Message, StringComparison.Ordinal);
        }

        // Nhưng vẫn phải có dòng audit để truy vết được ai đã ghi nhận phản hồi nào.
        var feedback = await response.Content.ReadFromJsonAsync<FeedbackDto>(ApiFactory.Json);
        Assert.Contains(_fx.Factory.Logs.AuditEntries,
            e => e.Message.Contains("feedback.create") && e.Message.Contains(feedback!.Id.ToString()));
    }
}
