using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>modify_07 — UC-14 (Architecture v3.1): DSL chạy trên read model, lọc visibility, board auto_add_query.</summary>
[Collection(ApiCollection.Name)]
public sealed class SearchTests
{
    private readonly ApiFixture _fx;

    public SearchTests(ApiFixture fx) => _fx = fx;

    private async Task<string> NewProjectAsync(string prefix)
    {
        var admin = await _fx.AdminAsync();
        var slug = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        (await admin.PostAsJsonAsync("/api/projects", new { slug, name = "Project " + prefix }, ApiFactory.Json)).EnsureSuccessStatusCode();
        await _fx.GrantProjectAccessAsync(admin, slug);
        return slug;
    }

    private static IEnumerable<int> Numbers(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(e => I(e, "number"));

    [Fact]
    public async Task Dsl_filters_state_label_assignee_author_type_dates_and_free_text()
    {
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();
        var slug = await NewProjectAsync("dsl");

        var a = await CreateTicketAsync(support, "Lỗi đăng nhập trên iOS", "app crash khi login", project: slug, extra: new { labels = new[] { "bug" }, assignees = new[] { "support" }, type = "Bug" });
        var b = await CreateTicketAsync(customer, "Đề xuất dark mode", "muốn có giao diện tối", project: slug);
        var c = await CreateTicketAsync(support, "Câu hỏi về thanh toán", "hoá đơn tháng 8", project: slug, extra: new { labels = new[] { "question", "bug" } });
        (await PatchAsync(support, I(c, "number"), new { state = "closed", stateReason = "not_planned" }, project: slug)).EnsureSuccessStatusCode();

        async Task<List<int>> Q(string q) => Numbers(await ReadJsonAsync(await support.GetAsync($"/api/projects/{slug}/tickets?state=all&q={Uri.EscapeDataString(q)}"))).ToList();

        Assert.Equal(new[] { I(b, "number"), I(a, "number") }, await Q("is:open"));
        Assert.Equal(new[] { I(c, "number") }, await Q("is:closed reason:\"not planned\""));
        Assert.Equal(new[] { I(c, "number"), I(a, "number") }, await Q("label:bug"));
        Assert.Equal(new[] { I(a, "number") }, await Q("label:bug -label:question"));
        Assert.Equal(new[] { I(c, "number"), I(b, "number"), I(a, "number") }, await Q("label:bug,question OR author:customer"));
        Assert.Equal(new[] { I(a, "number") }, await Q("assignee:@me"));
        Assert.Equal(new[] { I(c, "number"), I(b, "number") }, await Q("no:assignee"));
        Assert.Equal(new[] { I(b, "number") }, await Q("author:customer"));
        Assert.Equal(new[] { I(a, "number") }, await Q("type:bug"));
        Assert.Equal(new[] { I(a, "number") }, await Q("đăng nhập"));
        Assert.Equal(new[] { I(a, "number") }, await Q("crash in:body"));
        Assert.Equal(new[] { I(c, "number"), I(b, "number"), I(a, "number") }, await Q("created:>2020-01-01"));
        Assert.Empty(await Q("created:<2020-01-01"));
        Assert.Equal(new[] { I(a, "number"), I(b, "number"), I(c, "number") }, await Q("sort:created-asc"));

        // Cú pháp sai / qualifier lạ → 400.
        Assert.Equal(HttpStatusCode.BadRequest, (await support.GetAsync($"/api/projects/{slug}/tickets?q=(is:open")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await support.GetAsync($"/api/projects/{slug}/tickets?q=foo:bar")).StatusCode);
    }

    [Fact]
    public async Task Global_search_respects_internal_visibility_in_comments_and_caps_total()
    {
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();
        var slug = await NewProjectAsync("gsearch");
        var marker = "zebra" + Guid.NewGuid().ToString("N")[..6];
        var t = await CreateTicketAsync(customer, "ticket có comment", project: slug);
        var n = I(t, "number");

        await CommentAsync(support, n, $"công khai {marker}-public", slug);
        (await support.SendAsync(Post($"/api/projects/{slug}/tickets/{n}/internal-notes", new { body = $"nội bộ {marker}-secret" }))).EnsureSuccessStatusCode();

        // Projector chạy nền → chờ.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonElement staff = default;
        while (DateTime.UtcNow < deadline)
        {
            staff = await ReadJsonAsync(await support.GetAsync($"/api/search/tickets?q={Uri.EscapeDataString($"{marker}-secret in:comments repo:{slug}")}"));
            if (staff.GetProperty("totalCount").GetInt32() == 1) break;
            await Task.Delay(250);
        }
        Assert.Equal(1, staff.GetProperty("totalCount").GetInt32());

        var customerSecret = await ReadJsonAsync(await customer.GetAsync($"/api/search/tickets?q={Uri.EscapeDataString($"{marker}-secret in:comments repo:{slug}")}"));
        Assert.Equal(0, customerSecret.GetProperty("totalCount").GetInt32());
        var customerPublic = await ReadJsonAsync(await customer.GetAsync($"/api/search/tickets?q={Uri.EscapeDataString($"{marker}-public in:comments repo:{slug}")}"));
        Assert.Equal(1, customerPublic.GetProperty("totalCount").GetInt32());

        var commenter = await ReadJsonAsync(await customer.GetAsync($"/api/search/tickets?q={Uri.EscapeDataString($"commenter:support repo:{slug}")}"));
        Assert.Equal(1, commenter.GetProperty("totalCount").GetInt32());

        // Phân trang cursor (offset) trên kết quả tìm kiếm.
        var page1 = await ReadJsonAsync(await support.GetAsync($"/api/search/tickets?q={Uri.EscapeDataString("is:open")}&per_page=1"));
        Assert.Single(page1.GetProperty("items").EnumerateArray());
        Assert.NotNull(page1.GetProperty("nextCursor").GetString());
        Assert.True(page1.GetProperty("totalCount").GetInt32() <= 1000);
    }

    [Fact]
    public async Task Board_auto_add_query_adds_matching_tickets()
    {
        var responder = await _fx.ResponderAsync();
        var slug = await NewProjectAsync("autoadd");
        var board = await ReadJsonAsync(await responder.PostAsJsonAsync("/api/boards", new { name = "Auto " + Guid.NewGuid().ToString("N")[..6] }, ApiFactory.Json));
        var boardId = S(board, "id");
        var done = board.GetProperty("columns")[2].GetProperty("id").GetString();
        (await responder.PatchAsJsonAsync($"/api/boards/{boardId}", new
        {
            name = S(board, "name"),
            automation = new { item_closed_to_column_id = done, auto_add_query = $"repo:{slug} label:bug" }
        }, ApiFactory.Json)).EnsureSuccessStatusCode();

        var bug = await CreateTicketAsync(responder, "auto bug", project: slug, extra: new { labels = new[] { "bug" } });
        var plain = await CreateTicketAsync(responder, "auto plain", project: slug);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonElement detail = default;
        while (DateTime.UtcNow < deadline)
        {
            detail = await ReadJsonAsync(await responder.GetAsync($"/api/boards/{boardId}"));
            if (detail.GetProperty("items").GetArrayLength() >= 1) break;
            await Task.Delay(250);
        }
        var items = detail.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(I(bug, "number"), items[0].GetProperty("ticket").GetProperty("number").GetInt32());
        Assert.DoesNotContain(items, i => i.GetProperty("ticket").GetProperty("number").GetInt32() == I(plain, "number"));

        // Gắn label bug sau đó → cũng được thêm.
        (await PatchAsync(responder, I(plain, "number"), new { labels = new[] { "bug" } }, project: slug)).EnsureSuccessStatusCode();
        deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            detail = await ReadJsonAsync(await responder.GetAsync($"/api/boards/{boardId}"));
            if (detail.GetProperty("items").GetArrayLength() == 2) break;
            await Task.Delay(250);
        }
        Assert.Equal(2, detail.GetProperty("items").GetArrayLength());
    }
}
