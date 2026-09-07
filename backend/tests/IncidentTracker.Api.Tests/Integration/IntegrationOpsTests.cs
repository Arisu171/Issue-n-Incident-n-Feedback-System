using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IncidentTracker.Api.Modules.Tickets.Sla;
using Microsoft.Extensions.DependencyInjection;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>modify_08 — 6.7 Webhook, UC-16 SLA, BR-EV-05/BR-SEC-04 storage, mục 7 health (Architecture v3.1).</summary>
[Collection(ApiCollection.Name)]
public sealed class IntegrationOpsTests
{
    private readonly ApiFixture _fx;

    public IntegrationOpsTests(ApiFixture fx) => _fx = fx;

    private async Task<string> NewProjectAsync(string prefix)
    {
        var admin = await _fx.AdminAsync();
        var slug = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.PostAsJsonAsync("/api/projects", new { slug, name = "Project " + prefix }, ApiFactory.Json)).EnsureSuccessStatusCode();
        await _fx.GrantProjectAccessAsync(admin, slug);
        return slug;
    }

    [Fact]
    public async Task Webhook_delivers_signed_payload_skips_internal_notes_and_supports_redeliver_and_ping()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();
        var slug = await NewProjectAsync("hook");
        var url = $"http://receiver.local/{slug}";

        // Support (Triage) không được đăng ký webhook; Admin được; URL loopback bị chặn khi không cho phép... (dev cho phép).
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync($"/api/projects/{slug}/webhooks", new { targetUrl = url }, ApiFactory.Json)).StatusCode);
        var created = await ReadJsonAsync(await admin.PostAsJsonAsync($"/api/projects/{slug}/webhooks", new { targetUrl = url, events = new[] { "issues", "issue_comment", "label" } }, ApiFactory.Json));
        var hookId = S(created, "id");
        var secret = S(created, "secret");
        Assert.False(string.IsNullOrEmpty(secret));
        // Secret không hiển thị lại.
        var listed = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/webhooks"));
        Assert.Equal(JsonValueKind.Null, listed[0].GetProperty("secret").ValueKind);

        var t = await CreateTicketAsync(customer, "hook me", project: slug);
        var n = I(t, "number");
        (await support.SendAsync(Post($"/api/projects/{slug}/tickets/{n}/internal-notes", new { body = "nội bộ" }))).EnsureSuccessStatusCode();
        await CommentAsync(support, n, "công khai", slug);
        (await PatchAsync(support, n, new { state = "closed" }, project: slug)).EnsureSuccessStatusCode();

        // Chờ 3 delivery: issues.opened, issue_comment.created, issues.closed (consumer nền có thể retry khi va chạm).
        static List<string> EventsOf(IEnumerable<FakeWebhookReceiver.Captured> reqs)
            => reqs.Select(r => r.Headers["X-Event"] + "." + JsonDocument.Parse(r.Body).RootElement.GetProperty("action").GetString()).ToList();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        List<string> events = new();
        while (DateTime.UtcNow < deadline)
        {
            events = EventsOf(_fx.Factory.WebhookReceiver.Requests.Where(r => r.Url == url));
            if (events.Contains("issues.opened") && events.Contains("issue_comment.created") && events.Contains("issues.closed")) break;
            await Task.Delay(250);
        }
        var received = _fx.Factory.WebhookReceiver.Requests.Where(r => r.Url == url).ToList();
        events = EventsOf(received);
        Assert.Contains("issues.opened", events);
        Assert.Contains("issue_comment.created", events);
        Assert.Contains("issues.closed", events);
        Assert.DoesNotContain(received, r => r.Body.Contains("nội bộ"));

        // Chữ ký HMAC-SHA256 hợp lệ, header đúng tên.
        var first = received[0];
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(first.Body))).ToLowerInvariant();
        Assert.Equal(expected, first.Headers["X-Hub-Signature-256"]);
        Assert.True(Guid.TryParse(first.Headers["X-Delivery-Id"], out _));
        Assert.Equal(hookId, first.Headers["X-Hook-Id"]);
        Assert.Equal("UnifiedTicketing-Hookshot/1.0", first.Headers["User-Agent"]);

        var body = JsonDocument.Parse(first.Body).RootElement;
        Assert.Equal(n, body.GetProperty("ticket").GetProperty("number").GetInt32());
        Assert.Equal(slug, body.GetProperty("project").GetProperty("slug").GetString());

        // Delivery log + redeliver + ping. Receiver nhận request trước khi transaction consumer commit,
        // nên chờ thêm tới khi log delivery ghi đủ 3 hàng.
        JsonElement deliveries = default;
        deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            deliveries = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/webhooks/{hookId}/deliveries"));
            if (deliveries.GetArrayLength() >= 3 && deliveries.EnumerateArray().All(d => d.GetProperty("httpStatus").ValueKind == JsonValueKind.Number)) break;
            await Task.Delay(250);
        }
        Assert.True(deliveries.GetArrayLength() >= 3, $"deliveries={deliveries.GetArrayLength()}");
        Assert.All(deliveries.EnumerateArray(), d => Assert.Equal(200, d.GetProperty("httpStatus").GetInt32()));
        var deliveryId = S(deliveries[0], "id");
        var redeliver = await admin.PostAsync($"/api/projects/{slug}/webhooks/{hookId}/deliveries/{deliveryId}/redeliver", null);
        Assert.Equal(HttpStatusCode.Accepted, redeliver.StatusCode);
        var ping = await admin.PostAsync($"/api/projects/{slug}/webhooks/{hookId}/pings", null);
        Assert.Equal(HttpStatusCode.Accepted, ping.StatusCode);

        deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var reqs = _fx.Factory.WebhookReceiver.Requests.Where(r => r.Url == url).ToList();
            if (reqs.Any(r => r.Headers["X-Event"] == "ping") && reqs.Count >= 5) break;
            await Task.Delay(250);
        }
        var all = _fx.Factory.WebhookReceiver.Requests.Where(r => r.Url == url).ToList();
        Assert.Contains(all, r => r.Headers["X-Event"] == "ping");
        var redelivered = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/webhooks/{hookId}/deliveries"));
        Assert.Contains(redelivered.EnumerateArray(), d => d.GetProperty("isRedelivery").GetBoolean() && d.GetProperty("httpStatus").GetInt32() == 200);

        // Label CRUD phát event "label".
        (await admin.PostAsJsonAsync($"/api/projects/{slug}/labels", new { name = "hooked", color = "112233" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (_fx.Factory.WebhookReceiver.Requests.Any(r => r.Url == url && r.Headers["X-Event"] == "label")) break;
            await Task.Delay(250);
        }
        Assert.Contains(_fx.Factory.WebhookReceiver.Requests, r => r.Url == url && r.Headers["X-Event"] == "label");
    }

    [Fact]
    public async Task Webhook_failure_schedules_retry_with_backoff()
    {
        var admin = await _fx.AdminAsync();
        var slug = await NewProjectAsync("hookfail");
        var url = $"http://receiver.local/{slug}/fail";
        _fx.Factory.WebhookReceiver.StatusFor = u => u.EndsWith("/fail") ? 503 : 200;
        try
        {
            var created = await ReadJsonAsync(await admin.PostAsJsonAsync($"/api/projects/{slug}/webhooks", new { targetUrl = url }, ApiFactory.Json));
            var hookId = S(created, "id");
            await admin.PostAsync($"/api/projects/{slug}/webhooks/{hookId}/pings", null);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            JsonElement deliveries = default;
            while (DateTime.UtcNow < deadline)
            {
                deliveries = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/webhooks/{hookId}/deliveries"));
                if (deliveries.GetArrayLength() > 0 && deliveries[0].GetProperty("httpStatus").ValueKind == JsonValueKind.Number) break;
                await Task.Delay(250);
            }
            var d = deliveries[0];
            Assert.Equal(503, d.GetProperty("httpStatus").GetInt32());
            Assert.Equal(JsonValueKind.Null, d.GetProperty("deliveredAt").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, d.GetProperty("nextAttemptAt").ValueKind); // retry đã lên lịch (1 phút)
            var hooks = await ReadJsonAsync(await admin.GetAsync($"/api/projects/{slug}/webhooks"));
            Assert.Equal(1, hooks[0].GetProperty("consecutiveFailures").GetInt32());
        }
        finally
        {
            _fx.Factory.WebhookReceiver.StatusFor = _ => 200;
        }
    }

    [Fact]
    public async Task Sla_scheduler_writes_breach_and_escalation_once_and_notifies()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();
        var slug = await NewProjectAsync("sla");

        // P0: phản hồi 0 phút, escalate sau 0 phút → quá hạn ngay khi tạo.
        (await admin.PutAsJsonAsync("/api/sla-policies/P0", new { responseTimeMinutes = 0, resolutionTimeMinutes = 60, escalateAfterMinutes = 0, isActive = true }, ApiFactory.Json)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PutAsJsonAsync("/api/sla-policies/P1", new { responseTimeMinutes = 1 }, ApiFactory.Json)).StatusCode);

        var t = await CreateTicketAsync(support, "sla ticket", project: slug, extra: new { priority = "P0", assignees = new[] { "support" } });
        var n = I(t, "number");
        Assert.NotEqual(JsonValueKind.Null, t.GetProperty("slaDueAt").ValueKind);

        var scheduler = _fx.Factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<TicketSlaScheduler>().Single();
        var first = await scheduler.ScanAsync(CancellationToken.None);
        Assert.True(first.Breached >= 1);
        Assert.True(first.Escalated >= 1);
        var second = await scheduler.ScanAsync(CancellationToken.None);
        Assert.Equal((0, 0, 0), second); // idempotent

        var timeline = await TimelineAsync(support, n, slug);
        Assert.Single(timeline, e => S(e, "eventType") == "SLA_BREACHED");
        var escalated = Assert.Single(timeline, e => S(e, "eventType") == "ESCALATED");
        Assert.Contains("admin", escalated.GetProperty("payload").GetProperty("escalated_to").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(JsonValueKind.Null, escalated.GetProperty("actor").ValueKind); // actor = hệ thống

        // Admin (Lead) nhận thread reason SLA_BREACH.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonElement inbox = default;
        while (DateTime.UtcNow < deadline)
        {
            inbox = await ReadJsonAsync(await admin.GetAsync("/api/notifications?all=true"));
            if (inbox.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("ticket").GetProperty("id").GetString() == S(t, "id") && S(i, "reason") == "SLA_BREACH")) break;
            await Task.Delay(250);
        }
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(), i => i.GetProperty("ticket").GetProperty("id").GetString() == S(t, "id") && S(i, "reason") == "SLA_BREACH");

        // Nhân viên phản hồi → FIRST_RESPONSE → không quét nữa.
        await CommentAsync(support, n, "đang xử lý", slug);
        // Comment của chính người tạo (support) không phải "phản hồi cho khách"; tạo ticket khác của khách để kiểm FIRST_RESPONSE.
        var t2 = await CreateTicketAsync(customer, "sla customer", project: slug);
        (await PatchAsync(support, I(t2, "number"), new { priority = "P0" }, project: slug)).EnsureSuccessStatusCode();
        await CommentAsync(support, I(t2, "number"), "đã nhận", slug);
        var after = await scheduler.ScanAsync(CancellationToken.None);
        Assert.DoesNotContain(await TimelineAsync(support, I(t2, "number"), slug), e => S(e, "eventType") == "SLA_BREACHED");
    }

    /// <summary>
    /// U5 — khoá tệp xếp theo project.
    ///
    /// Trước đây khoá là <c>{yyyy/MM}/…</c> nên trên đĩa mọi tệp của mọi project trộn chung. Test
    /// khoá cả ba nhánh: có project, không có project, và project bịa.
    /// </summary>
    [Fact]
    public async Task Khoa_tep_xep_theo_project()
    {
        var customer = await _fx.CustomerAsync();

        var withProject = await ReadJsonAsync(await customer.GetAsync(
            "/api/storage/presigned-url?file_name=a.png&content_type=image/png&content_length=10&project=support"));
        Assert.StartsWith("projects/support/", S(withProject, "key"));

        // Không thuộc project nào (ảnh đại diện) — vẫn phải có tiền tố rõ nghĩa, không rơi ra gốc.
        var shared = await ReadJsonAsync(await customer.GetAsync(
            "/api/storage/presigned-url?file_name=a.png&content_type=image/png&content_length=10"));
        Assert.StartsWith("shared/", S(shared, "key"));

        // Slug bịa không được tạo ra thư mục ma, và `..` không được lọt vào khoá.
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync(
            "/api/storage/presigned-url?file_name=a.png&content_type=image/png&content_length=10&project=khong-co-project")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync(
            "/api/storage/presigned-url?file_name=a.png&content_type=image/png&content_length=10&project=..")).StatusCode);
    }

    [Fact]
    public async Task Storage_presigned_local_upload_enforces_type_and_size_then_serves_file()
    {
        var customer = await _fx.CustomerAsync();
        var anonymous = _fx.Factory.CreateClient();

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await customer.GetAsync("/api/storage/presigned-url?file_name=x.exe&content_type=application/x-msdownload&content_length=10")).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await customer.GetAsync("/api/storage/presigned-url?file_name=big.png&content_type=image/png&content_length=99999999999")).StatusCode);

        var content = Encoding.UTF8.GetBytes("hello attachment");
        var presigned = await ReadJsonAsync(await customer.GetAsync($"/api/storage/presigned-url?file_name=note.txt&content_type=text/plain&content_length={content.Length}"));
        Assert.Equal("PUT", S(presigned, "method"));
        var uploadUrl = new Uri(S(presigned, "uploadUrl"));

        // Sai Content-Type so với URL ký → 415.
        using (var wrong = new ByteArrayContent(content))
        {
            wrong.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await anonymous.PutAsync(uploadUrl.PathAndQuery, wrong)).StatusCode);
        }
        // Đúng → 200, key trả về.
        using (var ok = new ByteArrayContent(content))
        {
            ok.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            var response = await anonymous.PutAsync(uploadUrl.PathAndQuery, ok);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await ReadJsonAsync(response);
            Assert.Equal(S(presigned, "key"), S(result, "key"));
        }
        // Token dùng lại với nội dung dài hơn → 413.
        using (var tooBig = new ByteArrayContent(Encoding.UTF8.GetBytes("hello attachment and more")))
        {
            tooBig.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await anonymous.PutAsync(uploadUrl.PathAndQuery, tooBig)).StatusCode);
        }

        var served = await customer.GetAsync($"/api/storage/files/{S(presigned, "key")}");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("hello attachment", await served.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/storage/files/{S(presigned, "key")}")).StatusCode);

        // Đường ký: thẻ `img` của trình duyệt không gửi được header Authorization, nên ảnh phải
        // lấy được bằng chữ ký trên chính đường dẫn — nếu không thì mọi ảnh đã tải lên đều hỏng.
        var publicUrl = new Uri(S(presigned, "publicUrl"));
        Assert.Contains("/api/storage/public/", publicUrl.AbsolutePath);
        var viaSignature = await anonymous.GetAsync(publicUrl.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, viaSignature.StatusCode);
        Assert.Equal("hello attachment", await viaSignature.Content.ReadAsStringAsync());

        // Sửa chữ ký, hoặc bỏ hẳn, thì không lấy được — và trả 404 chứ không 403, vì 403 đã xác
        // nhận khoá đó có tệp thật.
        Assert.Equal(HttpStatusCode.NotFound,
            (await anonymous.GetAsync(publicUrl.AbsolutePath + "?t=deadbeef")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(publicUrl.AbsolutePath)).StatusCode);

        // Và chữ ký của tệp này không mở được tệp khác: nó ký theo khoá.
        var other = await ReadJsonAsync(await customer.GetAsync(
            "/api/storage/presigned-url?file_name=khac.txt&content_type=text/plain&content_length=5"));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(
            $"/api/storage/public/{S(other, "key")}{publicUrl.Query}")).StatusCode);
    }

    [Fact]
    public async Task Health_live_and_ready_endpoints_respond()
    {
        var anonymous = _fx.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health/live")).StatusCode);
        var ready = await anonymous.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var body = await ready.Content.ReadAsStringAsync();
        Assert.Contains("db", body);
        Assert.Contains("masstransit", body);
    }
}
