using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>modify_06 — UC-12/13, BR-SOCIAL-01..03, mục 2.8 (Architecture v3.1).</summary>
[Collection(ApiCollection.Name)]
public sealed class SocialTests
{
    private readonly ApiFixture _fx;

    public SocialTests(ApiFixture fx) => _fx = fx;

    private async Task<string> NewProjectAsync(string prefix)
    {
        var slug = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        await EnsureProjectAsync(_fx.Factory, slug, "Project " + prefix);
        return slug;
    }

    /// <summary>
    /// Reaction của **chính người xem** phải sống qua một lần tải lại trang.
    ///
    /// Bảng đếm gộp sẵn nói "có bao nhiêu người thả", không nói bạn có nằm trong số đó không —
    /// nên nếu câu trả lời của ticket không mang theo phần của người xem thì nút chỉ sáng từ lúc
    /// bấm cho tới lần tải kế tiếp, rồi tối lại như chưa từng thả.
    /// </summary>
    [Fact]
    public async Task Ticket_mang_theo_reaction_cua_chinh_nguoi_xem()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var slug = await NewProjectAsync("react-viewer");
        var ticket = await CreateTicketAsync(support, "Ticket có reaction", project: slug);
        var number = I(ticket, "number");

        (await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{number}/reactions/toggle",
            new { content = "+1" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        (await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{number}/reactions/toggle",
            new { content = "heart" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        (await responder.PostAsJsonAsync($"/api/projects/{slug}/tickets/{number}/reactions/toggle",
            new { content = "+1" }, ApiFactory.Json)).EnsureSuccessStatusCode();

        // Người thả thấy đúng phần của mình — **hai** emoji, vì một người giữ được nhiều loại.
        var asSupport = await GetTicketAsync(support, number, slug);
        var mine = asSupport.GetProperty("viewerReactions").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Contains("THUMBS_UP", mine);
        Assert.Contains("HEART", mine);

        // Người khác chỉ thấy phần của họ, dù bảng đếm là chung.
        var asResponder = await GetTicketAsync(responder, number, slug);
        Assert.Equal(new[] { "THUMBS_UP" },
            asResponder.GetProperty("viewerReactions").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(2, asResponder.GetProperty("reactions").GetProperty("THUMBS_UP").GetInt32());

        // Và trong **danh sách** cũng vậy — đó là đường nạp theo lô, dễ quên nhất.
        var list = await ReadJsonAsync(await responder.GetAsync($"/api/projects/{slug}/tickets?state=all&per_page=25"));
        var row = list.GetProperty("items").EnumerateArray().Single(x => I(x, "number") == number);
        Assert.Equal(new[] { "THUMBS_UP" },
            row.GetProperty("viewerReactions").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public async Task Reactions_are_unique_per_user_toggle_and_blocked_when_locked()
    {
        var customer = await _fx.CustomerAsync();
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var slug = await NewProjectAsync("react");
        var t = await CreateTicketAsync(support, "react me", project: slug);
        var n = I(t, "number");

        var first = await ReadJsonAsync(await customer.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/reactions", new { content = "+1" }, ApiFactory.Json));
        Assert.Equal(1, first.GetProperty("counts").GetProperty("THUMBS_UP").GetInt32());
        // Lặp lại cùng loại: idempotent (vẫn 1).
        var again = await ReadJsonAsync(await customer.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/reactions", new { content = "thumbs_up" }, ApiFactory.Json));
        Assert.Equal(1, again.GetProperty("counts").GetProperty("THUMBS_UP").GetInt32());
        Assert.Contains("THUMBS_UP", again.GetProperty("viewerReactions").EnumerateArray().Select(x => x.GetString()));

        var supportReact = await ReadJsonAsync(await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/reactions", new { content = "heart" }, ApiFactory.Json));
        Assert.Equal(2, supportReact.GetProperty("total").GetInt32());

        // Snapshot ticket có reactions_summary.
        var snapshot = await GetTicketAsync(customer, n, slug);
        Assert.True(snapshot.GetProperty("reactions").TryGetProperty("HEART", out var heart), "reactions=" + snapshot.GetProperty("reactions").GetRawText() + " supportReact=" + supportReact.GetRawText());
        Assert.Equal(1, heart.GetInt32());

        // Toggle: bấm lại = gỡ.
        var toggled = await ReadJsonAsync(await customer.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/reactions/toggle", new { content = "+1" }, ApiFactory.Json));
        Assert.False(toggled.GetProperty("counts").TryGetProperty("THUMBS_UP", out _));

        // Reaction trên comment hiện trong timeline.
        var comment = await CommentAsync(support, n, "hello", slug);
        await customer.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/comments/{S(comment, "id")}/reactions", new { content = "rocket" }, ApiFactory.Json);
        var row = Assert.Single(await TimelineAsync(customer, n, slug), e => S(e, "id") == S(comment, "id"));
        Assert.Equal(1, row.GetProperty("reactions").GetProperty("ROCKET").GetInt32());
        Assert.Contains("ROCKET", row.GetProperty("viewerReactions").EnumerateArray().Select(x => x.GetString()));

        // Loại không hợp lệ → 400; khoá → 403 với mọi người kể cả Write.
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/reactions", new { content = "party" }, ApiFactory.Json)).StatusCode);
        (await responder.PutAsJsonAsync($"/api/projects/{slug}/tickets/{n}/lock", new { lockReason = "resolved" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await responder.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/reactions", new { content = "eyes" }, ApiFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task Auto_subscribe_and_notification_thread_per_user_per_ticket()
    {
        var customer = await _fx.CustomerAsync();
        var support = await _fx.SupportAsync();
        var slug = await NewProjectAsync("notif");
        var t = await CreateTicketAsync(customer, "notify me", project: slug);
        var n = I(t, "number");
        var ticketId = Guid.Parse(S(t, "id"));

        // Tác giả được auto-subscribe (worker chạy nền).
        await WaitAsync(async () => (await ReadJsonAsync(await customer.GetAsync($"/api/notifications/threads/{ticketId}/subscription"))).GetProperty("subscribed").GetBoolean());
        var sub = await ReadJsonAsync(await customer.GetAsync($"/api/notifications/threads/{ticketId}/subscription"));
        Assert.Equal("AUTHOR", S(sub, "reason"));

        await CommentAsync(support, n, "trả lời 1", slug);
        await CommentAsync(support, n, "trả lời 2", slug);

        // Khách có ĐÚNG 1 thread cho ticket, unread, reason author; support (actor) không có thread.
        JsonElement inbox = default;
        await WaitAsync(async () =>
        {
            inbox = await ReadJsonAsync(await customer.GetAsync("/api/notifications"));
            return inbox.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId.ToString());
        });
        var threads = inbox.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId.ToString()).ToList();
        var thread = Assert.Single(threads);
        Assert.True(thread.GetProperty("unread").GetBoolean());
        Assert.Equal("AUTHOR", S(thread, "reason"));
        Assert.Equal("COMMENTED", S(thread, "lastEventType"));
        Assert.Equal("support", thread.GetProperty("lastActor").GetProperty("login").GetString());

        var supportInbox = await ReadJsonAsync(await support.GetAsync("/api/notifications?all=true"));
        Assert.DoesNotContain(supportInbox.GetProperty("items").EnumerateArray(), i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId.ToString());

        // Đọc → unread=false; unread-count giảm.
        var before = (await ReadJsonAsync(await customer.GetAsync("/api/notifications/unread-count"))).GetProperty("unreadCount").GetInt32();
        (await customer.PatchAsJsonAsync($"/api/notifications/threads/{S(thread, "id")}", new { unread = false }, ApiFactory.Json)).EnsureSuccessStatusCode();
        var after = (await ReadJsonAsync(await customer.GetAsync("/api/notifications/unread-count"))).GetProperty("unreadCount").GetInt32();
        Assert.Equal(before - 1, after);

        // Unsubscribe thủ công → comment mới không làm thread unread lại; @mention trực tiếp thì subscribe lại.
        (await customer.DeleteAsync($"/api/notifications/threads/{ticketId}/subscription")).EnsureSuccessStatusCode();

        // Vắt cạn thông báo còn trong hàng đợi từ hai bình luận trước khi bước vào phép thử.
        // Thiếu bước này thì một thông báo đến muộn sẽ bật lại `unread` sau khi ta vừa đánh dấu
        // đã đọc, và assert bên dưới đổ lỗi nhầm cho "trả lời 3" — đúng ca đã làm CI đỏ.
        await SettleReadAsync(customer, ticketId);

        await CommentAsync(support, n, "trả lời 3", slug);
        await Task.Delay(3000);
        var stillRead = await ReadJsonAsync(await customer.GetAsync("/api/notifications?all=true"));
        var mine = stillRead.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId.ToString());
        Assert.False(mine.GetProperty("unread").GetBoolean());

        await CommentAsync(support, n, "@customer bạn xem lại giúp", slug);
        await WaitAsync(async () => (await ReadJsonAsync(await customer.GetAsync($"/api/notifications/threads/{ticketId}/subscription"))).GetProperty("subscribed").GetBoolean());
        await WaitAsync(async () =>
        {
            var inbox2 = await ReadJsonAsync(await customer.GetAsync("/api/notifications"));
            return inbox2.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId.ToString() && S(i, "reason") == "MENTION");
        });

        // Done → biến khỏi inbox mặc định, còn trong ?done=true.
        (await customer.DeleteAsync($"/api/notifications/threads/{S(thread, "id")}")).EnsureSuccessStatusCode();
        var def = await ReadJsonAsync(await customer.GetAsync("/api/notifications?all=true"));
        Assert.DoesNotContain(def.GetProperty("items").EnumerateArray(), i => S(i, "id") == S(thread, "id"));
        var done = await ReadJsonAsync(await customer.GetAsync("/api/notifications?done=true"));
        Assert.Contains(done.GetProperty("items").EnumerateArray(), i => S(i, "id") == S(thread, "id"));
    }

    /// <summary>
    /// Tắt thông báo của một project thì **im thật**, kể cả trên ticket mình đang tự động theo dõi.
    ///
    /// Đây là chỗ luật cũ hụt: `WatchLevel.Ignore` được ghi vào cơ sở dữ liệu nhưng không nơi nào
    /// đọc — chỉ nhánh `All` được đọc, và chỉ để *thêm* người nhận. Nên tắt xong vẫn nhận đủ, vì
    /// đăng ký ở mức ticket (tác giả, được giao, đã bình luận) vẫn còn nguyên và tự nó đưa mình
    /// vào danh sách.
    /// </summary>
    [Fact]
    public async Task Tat_thong_bao_project_thi_khong_con_nhan_gi_tru_khi_bi_goi_ten()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var slug = await NewProjectAsync("im-lang");

        // `support` là tác giả nên được tự động theo dõi ticket — đúng bối cảnh của lỗi.
        var t = await CreateTicketAsync(support, "ticket bị tắt thông báo", project: slug);
        var n = I(t, "number");
        var ticketId = S(t, "id");

        (await support.PutAsJsonAsync($"/api/projects/{slug}/subscription",
            new { level = "ignore" }, ApiFactory.Json)).EnsureSuccessStatusCode();

        await CommentAsync(responder, n, "người khác bình luận", slug);

        // Không có gì để chờ cho tới khi *xuất hiện*, nên chờ một mốc khác: bình luận thứ hai có
        // gọi tên, và khi nó tới thì bình luận thứ nhất chắc chắn đã xử lý xong.
        await CommentAsync(responder, n, $"@{ApiFixture.SupportEmail.Split('@')[0]} xem giúp", slug);

        await WaitAsync(async () =>
        {
            var inbox = await ReadJsonAsync(await support.GetAsync("/api/notifications?all=true"));
            return inbox.GetProperty("items").EnumerateArray()
                .Any(i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId);
        });

        var box = await ReadJsonAsync(await support.GetAsync("/api/notifications?all=true"));
        var thread = box.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId);

        // Chỉ còn đúng lời gọi tên. Bình luận thường đã bị chặn — nếu không thì `reason` ở đây là
        // AUTHOR/COMMENT chứ không phải MENTION.
        Assert.Equal("MENTION", S(thread, "reason"));
    }

    [Fact]
    public async Task Internal_note_never_notifies_customer_but_project_watcher_ALL_gets_public_events()
    {
        var customer = await _fx.CustomerAsync();
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var slug = await NewProjectAsync("watch");
        (await responder.PutAsJsonAsync($"/api/projects/{slug}/subscription", new { level = "all" }, ApiFactory.Json)).EnsureSuccessStatusCode();

        var t = await CreateTicketAsync(customer, "watched", project: slug);
        var n = I(t, "number");
        var ticketId = S(t, "id");

        (await support.SendAsync(Post($"/api/projects/{slug}/tickets/{n}/internal-notes", new { body = "nội bộ" }))).EnsureSuccessStatusCode();
        await CommentAsync(support, n, "công khai", slug);

        // Chờ tới khi thread của khách xuất hiện (worker nền có thể retry khi va chạm transaction).
        JsonElement customerThread = default;
        await WaitAsync(async () =>
        {
            var inbox = await ReadJsonAsync(await customer.GetAsync("/api/notifications?all=true"));
            var mine = inbox.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId).ToList();
            if (mine.Count == 0) return false;
            customerThread = mine.Single();
            return true;
        });
        Assert.Equal("COMMENTED", S(customerThread, "lastEventType")); // không phải INTERNAL_NOTE

        await WaitAsync(async () =>
        {
            var inbox = await ReadJsonAsync(await responder.GetAsync("/api/notifications"));
            return inbox.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId);
        });

        var responderInbox = await ReadJsonAsync(await responder.GetAsync("/api/notifications"));
        var responderThread = responderInbox.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId);
        Assert.Equal("SUBSCRIBED", S(responderThread, "reason"));
    }

    /// <summary>
    /// Đánh dấu thread đã đọc rồi chờ tới khi trạng thái đó <b>ổn định</b>.
    ///
    /// Thông báo đi qua hàng đợi nên có độ trễ không xác định. Đánh dấu đã đọc một lần rồi
    /// khẳng định ngay là đua với những thông báo còn đang bay: chúng đến sau và bật lại
    /// `unread`. Ở đây đánh dấu lại mỗi khi thấy `unread`, và chỉ trả về khi đã thấy `unread`
    /// bằng false qua nhiều lần kiểm liên tiếp — tức hàng đợi đã cạn.
    /// </summary>
    private static async Task SettleReadAsync(HttpClient client, Guid ticketId, int stableChecks = 4, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var stable = 0;

        while (DateTime.UtcNow < deadline)
        {
            var inbox = await ReadJsonAsync(await client.GetAsync("/api/notifications?all=true"));
            var mine = inbox.GetProperty("items").EnumerateArray()
                .Where(i => i.GetProperty("ticket").GetProperty("id").GetString() == ticketId.ToString())
                .ToList();

            if (mine.Count == 1 && !mine[0].GetProperty("unread").GetBoolean())
            {
                if (++stable >= stableChecks) return;
            }
            else
            {
                stable = 0;
                if (mine.Count == 1)
                {
                    (await client.PatchAsJsonAsync($"/api/notifications/threads/{S(mine[0], "id")}",
                        new { unread = false }, ApiFactory.Json)).EnsureSuccessStatusCode();
                }
            }

            await Task.Delay(250);
        }

        Assert.Fail("Thread không về được trạng thái đã đọc ổn định trong thời gian chờ.");
    }

    private static async Task WaitAsync(Func<Task<bool>> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }
        Assert.Fail("Điều kiện không đạt trong thời gian chờ.");
    }
}
