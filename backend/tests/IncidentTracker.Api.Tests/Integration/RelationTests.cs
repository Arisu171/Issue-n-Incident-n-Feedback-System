using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>modify_05 — UC-10, BR-REL-02/03/04/06, BR-SOCIAL-04, AC02/AC02b (Architecture v3.1).</summary>
[Collection(ApiCollection.Name)]
public sealed class RelationTests
{
    private readonly ApiFixture _fx;

    public RelationTests(ApiFixture fx) => _fx = fx;

    private async Task<string> NewProjectAsync(string prefix)
    {
        var slug = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        await EnsureProjectAsync(_fx.Factory, slug, "Project " + prefix);
        return slug;
    }

    [Fact]
    public async Task Sub_issue_has_one_parent_no_cycle_and_pair_events()
    {
        var support = await _fx.SupportAsync();
        var slug = await NewProjectAsync("sub");
        var parent = await CreateTicketAsync(support, "parent", project: slug);
        var child = await CreateTicketAsync(support, "child", project: slug, extra: new { parentNumber = I(parent, "number") });
        Assert.Equal(I(parent, "number"), child.GetProperty("parent").GetProperty("number").GetInt32());

        var other = await CreateTicketAsync(support, "other parent", project: slug);
        // Con đã có cha → 422.
        var second = await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{I(other, "number")}/sub_issues", new { subIssue = $"#{I(child, "number")}" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        // Cycle: parent làm con của child → 422.
        var cycle = await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{I(child, "number")}/sub_issues", new { subIssue = $"#{I(parent, "number")}" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cycle.StatusCode);

        var subs = await ReadJsonAsync(await support.GetAsync($"/api/projects/{slug}/tickets/{I(parent, "number")}/sub_issues"));
        Assert.Single(subs.EnumerateArray());

        var parentTimeline = await TimelineAsync(support, I(parent, "number"), slug);
        Assert.Contains(parentTimeline, e => S(e, "eventType") == "SUB_ISSUE_ADDED");
        var childTimeline = await TimelineAsync(support, I(child, "number"), slug);
        Assert.Contains(childTimeline, e => S(e, "eventType") == "PARENT_ISSUE_ADDED");

        // Gỡ → cặp event REMOVED, parent = null.
        var remove = await support.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{slug}/tickets/{I(parent, "number")}/sub_issues")
        {
            Content = JsonContent.Create(new { subIssue = $"#{I(child, "number")}" }, options: ApiFactory.Json)
        });
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await GetTicketAsync(support, I(child, "number"), slug)).GetProperty("parent").ValueKind);
    }

    [Fact]
    public async Task Sub_issue_limits_100_children_and_8_levels()
    {
        var support = await _fx.SupportAsync();
        var slug = await NewProjectAsync("limits");
        var root = await CreateTicketAsync(support, "root", project: slug);

        // 8 cấp: root (1) + 7 con nối tiếp = 8; cấp thứ 9 → 422.
        var current = I(root, "number");
        for (var i = 0; i < 7; i++)
        {
            var next = await CreateTicketAsync(support, $"level {i + 2}", project: slug, extra: new { parentNumber = current });
            current = I(next, "number");
        }
        var ninth = await support.SendAsync(Post($"/api/projects/{slug}/tickets", new { title = "level 9", parentNumber = current }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ninth.StatusCode);

        // 100 con: chèn thẳng DB cho nhanh rồi gắn con thứ 101 qua API → 422.
        var parent = await CreateTicketAsync(support, "wide parent", project: slug);
        var parentId = Guid.Parse(S(parent, "id"));
        await using (var db = _fx.Factory.CreateDbContext())
        {
            var project = await db.Projects.SingleAsync(p => p.Slug == slug);
            var author = await db.Users.SingleAsync(u => u.Email == ApiFixture.SupportEmail);
            for (var i = 0; i < 100; i++)
            {
                db.Tickets.Add(new Ticket
                {
                    Id = Guid.NewGuid(), ProjectId = project.Id, Number = 10_000 + i, Title = $"bulk {i}", AuthorId = author.Id,
                    ParentTicketId = parentId, SubIssuePosition = i, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            await db.SaveChangesAsync();
        }
        var extra = await CreateTicketAsync(support, "101st", project: slug);
        var tooMany = await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{I(parent, "number")}/sub_issues", new { subIssue = $"#{I(extra, "number")}" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);

        var summary = (await GetTicketAsync(support, I(parent, "number"), slug)).GetProperty("subIssuesSummary");
        Assert.Equal(100, summary.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task AC02_closing_parent_with_open_sub_issue_warns_by_default_and_409_when_strict()
    {
        var support = await _fx.SupportAsync();
        var admin = await _fx.AdminAsync();
        var slug = await NewProjectAsync("ac02");
        var parent = await CreateTicketAsync(support, "AC02 parent", project: slug);
        var child = await CreateTicketAsync(support, "AC02 child", project: slug, extra: new { parentNumber = I(parent, "number") });

        // Mặc định (giống GitHub): 200 + warnings.
        var closed = await PatchAsync(support, I(parent, "number"), new { state = "closed" }, project: slug);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var body = await ReadJsonAsync(closed);
        Assert.Equal("CLOSED", S(body, "state"));
        Assert.Contains("OPEN_SUB_ISSUES", body.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Equal("OPEN", S(await GetTicketAsync(support, I(child, "number"), slug), "state"));
        Assert.Equal(0, body.GetProperty("subIssuesSummary").GetProperty("completed").GetInt32());

        // AC02b: strict_close_policy → 409 blocking_items.
        (await PatchAsync(support, I(parent, "number"), new { state = "open" }, project: slug)).EnsureSuccessStatusCode();
        (await admin.PatchAsJsonAsync($"/api/projects/{slug}", new { strictClosePolicy = true }, ApiFactory.Json)).EnsureSuccessStatusCode();
        var strict = await PatchAsync(support, I(parent, "number"), new { state = "closed" }, project: slug);
        Assert.Equal(HttpStatusCode.Conflict, strict.StatusCode);
        var problem = await ReadJsonAsync(strict);
        Assert.Single(problem.GetProperty("blocking_items").EnumerateArray());
        Assert.Equal("OPEN", S(await GetTicketAsync(support, I(parent, "number"), slug), "state"));
    }

    [Fact]
    public async Task Dependency_blocked_by_no_cycle_and_close_warns()
    {
        var support = await _fx.SupportAsync();
        var slug = await NewProjectAsync("dep");
        var a = await CreateTicketAsync(support, "A", project: slug);
        var b = await CreateTicketAsync(support, "B", project: slug);
        var c = await CreateTicketAsync(support, "C", project: slug);
        int na = I(a, "number"), nb = I(b, "number"), nc = I(c, "number");

        // A blocked by B, B blocked by C; C blocked by A → cycle 422.
        Assert.Equal(HttpStatusCode.Created, (await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{na}/dependencies/blocked_by", new { issue = $"#{nb}" }, ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{nb}/dependencies/blocked_by", new { issue = $"#{nc}" }, ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{nc}/dependencies/blocked_by", new { issue = $"#{na}" }, ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{na}/dependencies/blocked_by", new { issue = $"#{na}" }, ApiFactory.Json)).StatusCode);

        var blocking = await ReadJsonAsync(await support.GetAsync($"/api/projects/{slug}/tickets/{nb}/dependencies/blocking"));
        Assert.Equal(na, blocking[0].GetProperty("number").GetInt32());

        var timelineA = await TimelineAsync(support, na, slug);
        Assert.Contains(timelineA, e => S(e, "eventType") == "BLOCKED_BY_ADDED");
        var timelineB = await TimelineAsync(support, nb, slug);
        Assert.Contains(timelineB, e => S(e, "eventType") == "BLOCKING_ADDED");

        // Đóng A khi B còn mở: cho phép + warning (GitHub không chặn).
        var closed = await ReadJsonAsync(await PatchAsync(support, na, new { state = "closed" }, project: slug));
        Assert.Equal("CLOSED", S(closed, "state"));
        Assert.Contains("BLOCKED_BY_OPEN", closed.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()));

        var remove = await support.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{slug}/tickets/{na}/dependencies/blocked_by")
        {
            Content = JsonContent.Create(new { issue = $"#{nb}" }, options: ApiFactory.Json)
        });
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.Empty((await ReadJsonAsync(await support.GetAsync($"/api/projects/{slug}/tickets/{na}/dependencies/blocked_by"))).EnumerateArray());
    }

    [Fact]
    public async Task Mention_creates_actor_only_event_for_mentioned_user_and_cross_reference_on_target()
    {
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();
        var slug = await NewProjectAsync("mention");
        var target = await CreateTicketAsync(support, "target", project: slug);
        var source = await CreateTicketAsync(customer, "source", project: slug);
        int ns = I(source, "number"), nt = I(target, "number");

        await CommentAsync(support, ns, $"@customer xem thêm #{nt} nhé, cc @ghost-user", slug);
        // Mention trong ghi chú nội bộ tới khách hàng → không được thông báo (BR-SOCIAL-04).
        (await support.SendAsync(Post($"/api/projects/{slug}/tickets/{ns}/internal-notes", new { body = "@customer2 nội bộ" }))).EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        List<JsonElement> customerView = new();
        while (DateTime.UtcNow < deadline)
        {
            customerView = await TimelineAsync(customer, ns, slug);
            if (customerView.Any(e => S(e, "eventType") == "MENTIONED")) break;
            await Task.Delay(250);
        }

        var mentioned = Assert.Single(customerView, e => S(e, "eventType") == "MENTIONED");
        Assert.Equal("customer", mentioned.GetProperty("actor").GetProperty("login").GetString());
        Assert.Equal("ACTOR_ONLY", S(mentioned, "visibility"));

        // Người khác (support) không thấy MENTIONED của customer; customer2 không có MENTIONED nào.
        Assert.DoesNotContain(await TimelineAsync(support, ns, slug), e => S(e, "eventType") == "MENTIONED");
        var other = await _fx.OtherCustomerAsync();
        Assert.DoesNotContain(await TimelineAsync(other, ns, slug), e => S(e, "eventType") == "MENTIONED");

        var targetTimeline = await TimelineAsync(customer, nt, slug);
        var xref = Assert.Single(targetTimeline, e => S(e, "eventType") == "CROSS_REFERENCED");
        Assert.Equal(ns, xref.GetProperty("payload").GetProperty("source_number").GetInt32());

        // Link @mention trong HTML chỉ cho user thật.
        var comment = Assert.Single(customerView, e => S(e, "eventType") == "COMMENTED");
        // `/profiles/{login}` — trang hồ sơ thật. `/users` chỉ là tiền tố của API quản trị tài
        // khoản, không có màn hình nào, nên nhắc tên trỏ vào đó là mỗi lần bấm một cú 404.
        Assert.Contains("href=\"/profiles/customer\"", S(comment, "bodyHtml"));
        Assert.DoesNotContain("/profiles/ghost-user", S(comment, "bodyHtml"));
        Assert.Contains($"/projects/{slug}/issues/{nt}", S(comment, "bodyHtml"));
    }

    [Fact]
    public async Task Vcs_closing_keyword_connects_then_closes_on_merge()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();
        var slug = await NewProjectAsync("vcs");
        var t = await CreateTicketAsync(support, "fix me", project: slug);
        var n = I(t, "number");

        var opened = await ReadJsonAsync(await admin.PostAsJsonAsync($"/api/projects/{slug}/vcs/events",
            new { kind = "pull_request", url = "https://git.local/pr/7", prId = "7", title = "Fix login", message = $"Fixes #{n}", merged = false }, ApiFactory.Json));
        Assert.Single(opened.GetProperty("connected").EnumerateArray());
        Assert.Empty(opened.GetProperty("closed").EnumerateArray());
        Assert.Equal("OPEN", S(await GetTicketAsync(support, n, slug), "state"));

        var merged = await ReadJsonAsync(await admin.PostAsJsonAsync($"/api/projects/{slug}/vcs/events",
            new { kind = "pull_request", url = "https://git.local/pr/7", prId = "7", sha = "abc123", title = "Fix login", message = $"Fixes #{n}", merged = true }, ApiFactory.Json));
        Assert.Single(merged.GetProperty("closed").EnumerateArray());

        var after = await GetTicketAsync(support, n, slug);
        Assert.Equal("CLOSED", S(after, "state"));
        Assert.Equal("COMPLETED", S(after, "stateReason"));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("closedBy").ValueKind); // actor = hệ thống

        var timeline = await TimelineAsync(support, n, slug);
        Assert.Contains(timeline, e => S(e, "eventType") == "CONNECTED");
        var closedEvent = Assert.Single(timeline, e => S(e, "eventType") == "CLOSED");
        Assert.Equal("abc123", closedEvent.GetProperty("payload").GetProperty("commit_sha").GetString());

        // Commit chỉ nhắc #N → REFERENCED, không đóng.
        var t2 = await CreateTicketAsync(support, "ref only", project: slug);
        await admin.PostAsJsonAsync($"/api/projects/{slug}/vcs/events", new { kind = "commit", url = "https://git.local/c/def", sha = "def456", title = $"tweak see #{I(t2, "number")}", onDefaultBranch = true }, ApiFactory.Json);
        Assert.Equal("OPEN", S(await GetTicketAsync(support, I(t2, "number"), slug), "state"));
        Assert.Contains(await TimelineAsync(support, I(t2, "number"), slug), e => S(e, "eventType") == "REFERENCED");
    }
}
