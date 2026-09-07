using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>modify_03 — Architecture v3.1 mục 4.3 (AC01–AC06), 6.5, BR-CONC-01, BR-LIFECYCLE-*, BR-EDIT-01, BR-SEC-06.</summary>
[Collection(ApiCollection.Name)]
public sealed class TicketCoreTests
{
    private readonly ApiFixture _fx;

    public TicketCoreTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Create_assigns_sequential_numbers_and_sets_etag()
    {
        var support = await _fx.SupportAsync();

        var a = await CreateTicketAsync(support, "Số thứ tự A");
        var b = await CreateTicketAsync(support, "Số thứ tự B");

        Assert.Equal(I(a, "number") + 1, I(b, "number"));
        Assert.Equal("support", S(a, "projectSlug"));
        Assert.Equal("OPEN", S(a, "state"));
        Assert.Equal(1, I(a, "version"));

        var get = await support.GetAsync($"/api/projects/support/tickets/{I(a, "number")}");
        Assert.Equal("\"v1\"", get.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Create_without_idempotency_key_is_400()
    {
        var support = await _fx.SupportAsync();
        var response = await support.PostAsJsonAsync("/api/projects/support/tickets", new { title = "no key" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AC01_close_as_completed_sets_state_reason_closed_by_and_writes_event()
    {
        var support = await _fx.SupportAsync();
        var t = await CreateTicketAsync(support, "AC01 close");
        var n = I(t, "number");

        var response = await PatchAsync(support, n, new { state = "closed", stateReason = "completed" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var closed = await ReadJsonAsync(response);

        Assert.Equal("CLOSED", S(closed, "state"));
        Assert.Equal("COMPLETED", S(closed, "stateReason"));
        Assert.NotEqual(JsonValueKind.Null, closed.GetProperty("closedAt").ValueKind);
        Assert.Equal("support", closed.GetProperty("closedBy").GetProperty("login").GetString());

        var timeline = await TimelineAsync(support, n);
        Assert.Contains(timeline, e => S(e, "eventType") == "CLOSED");
    }

    [Fact]
    public async Task AC03_customer_comment_on_closed_ticket_does_not_reopen()
    {
        var customer = await _fx.CustomerAsync();
        var support = await _fx.SupportAsync();
        var t = await CreateTicketAsync(customer, "AC03 closed stays closed");
        var n = I(t, "number");
        (await PatchAsync(support, n, new { state = "closed", stateReason = "not_planned" })).EnsureSuccessStatusCode();

        await CommentAsync(customer, n, "vẫn còn lỗi nhé");

        var after = await GetTicketAsync(customer, n);
        Assert.Equal("CLOSED", S(after, "state"));
        Assert.Equal(1, I(after, "comments"));
    }

    [Fact]
    public async Task AC04_reopen_sets_reason_reopened_and_clears_closed_fields()
    {
        var support = await _fx.SupportAsync();
        var t = await CreateTicketAsync(support, "AC04 reopen");
        var n = I(t, "number");
        (await PatchAsync(support, n, new { state = "closed", stateReason = "not_planned" })).EnsureSuccessStatusCode();

        var reopened = await ReadJsonAsync(await PatchAsync(support, n, new { state = "open" }));
        Assert.Equal("OPEN", S(reopened, "state"));
        Assert.Equal("REOPENED", S(reopened, "stateReason"));
        Assert.Equal(JsonValueKind.Null, reopened.GetProperty("closedAt").ValueKind);

        var timeline = await TimelineAsync(support, n);
        Assert.Contains(timeline, e => S(e, "eventType") == "REOPENED");
    }

    [Fact]
    public async Task AC05_close_as_duplicate_links_original_and_cross_references_it()
    {
        var support = await _fx.SupportAsync();
        var original = await CreateTicketAsync(support, "AC05 original");
        var dup = await CreateTicketAsync(support, "AC05 duplicate");

        var missing = await PatchAsync(support, I(dup, "number"), new { state = "closed", stateReason = "duplicate" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var closed = await ReadJsonAsync(await PatchAsync(support, I(dup, "number"),
            new { state = "closed", stateReason = "duplicate", duplicateOf = $"#{I(original, "number")}" }));
        Assert.Equal("DUPLICATE", S(closed, "stateReason"));
        Assert.Equal(I(original, "number"), closed.GetProperty("duplicateOf").GetProperty("number").GetInt32());

        var dupTimeline = await TimelineAsync(support, I(dup, "number"));
        Assert.Contains(dupTimeline, e => S(e, "eventType") == "MARKED_AS_DUPLICATE");
        var originalTimeline = await TimelineAsync(support, I(original, "number"));
        Assert.Contains(originalTimeline, e => S(e, "eventType") == "CROSS_REFERENCED");
    }

    [Fact]
    public async Task AC06_locked_conversation_blocks_customer_comment_but_not_write_user()
    {
        var support = await _fx.SupportAsync();
        var responder = await _fx.ResponderAsync();
        var customer = await _fx.CustomerAsync();
        var t = await CreateTicketAsync(customer, "AC06 lock");
        var n = I(t, "number");

        // Support = Triage → không được khoá.
        var forbidden = await support.PutAsJsonAsync($"/api/projects/support/tickets/{n}/lock", new { lockReason = "too_heated" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var locked = await responder.PutAsJsonAsync($"/api/projects/support/tickets/{n}/lock", new { lockReason = "too_heated" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Equal("TOO_HEATED", S(await ReadJsonAsync(locked), "activeLockReason"));

        var customerComment = await customer.SendAsync(Post($"/api/projects/support/tickets/{n}/comments", new { body = "spam" }));
        Assert.Equal(HttpStatusCode.Forbidden, customerComment.StatusCode);

        await CommentAsync(responder, n, "Write vẫn bình luận được");

        (await responder.DeleteAsync($"/api/projects/support/tickets/{n}/lock")).EnsureSuccessStatusCode();
        await CommentAsync(customer, n, "mở khoá rồi");
    }

    [Fact]
    public async Task If_Match_mismatch_returns_412_and_correct_version_succeeds()
    {
        var support = await _fx.SupportAsync();
        var t = await CreateTicketAsync(support, "If-Match");
        var n = I(t, "number");

        var stale = await PatchAsync(support, n, new { title = "đổi tên" }, ifMatch: "\"v99\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var ok = await PatchAsync(support, n, new { title = "đổi tên" }, ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("\"v2\"", ok.Headers.ETag?.Tag);

        var timeline = await TimelineAsync(support, n);
        var renamed = Assert.Single(timeline, e => S(e, "eventType") == "RENAMED");
        Assert.Equal("If-Match", renamed.GetProperty("payload").GetProperty("from").GetString());
    }

    [Fact]
    public async Task Internal_note_is_hidden_from_customer_in_timeline_and_visible_to_staff()
    {
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();
        var t = await CreateTicketAsync(customer, "Internal note visibility");
        var n = I(t, "number");

        var note = await support.SendAsync(Post($"/api/projects/support/tickets/{n}/internal-notes", new { body = "kiểm tra DB <script>x</script>" }));
        Assert.Equal(HttpStatusCode.Created, note.StatusCode);

        var forbidden = await customer.SendAsync(Post($"/api/projects/support/tickets/{n}/internal-notes", new { body = "hack" }));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        await CommentAsync(support, n, "public reply");

        var customerView = await TimelineAsync(customer, n);
        Assert.DoesNotContain(customerView, e => S(e, "eventType") == "INTERNAL_NOTE");
        Assert.Contains(customerView, e => S(e, "eventType") == "COMMENTED");

        var staffView = await TimelineAsync(support, n);
        var internalNote = Assert.Single(staffView, e => S(e, "eventType") == "INTERNAL_NOTE");
        Assert.DoesNotContain("<script", S(internalNote, "bodyHtml"));
        Assert.Contains("kiểm tra DB", S(internalNote, "bodyHtml"));

        // FIRST_RESPONSE (mở rộng): nhân viên trả lời khách lần đầu → first_response_at có giá trị.
        var after = await GetTicketAsync(support, n);
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("firstResponseAt").ValueKind);
    }

    [Fact]
    public async Task Pin_is_limited_to_three_per_project()
    {
        var responder = await _fx.ResponderAsync();
        var slug = "pin-" + Guid.NewGuid().ToString("N")[..8];
        await EnsureProjectAsync(_fx.Factory, slug, "Pin project");

        var numbers = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            numbers.Add(I(await CreateTicketAsync(responder, $"pin {i}", project: slug), "number"));
        }

        for (var i = 0; i < 3; i++)
        {
            (await responder.PutAsync($"/api/projects/{slug}/tickets/{numbers[i]}/pin", null)).EnsureSuccessStatusCode();
        }

        var fourth = await responder.PutAsync($"/api/projects/{slug}/tickets/{numbers[3]}/pin", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, fourth.StatusCode);

        (await responder.DeleteAsync($"/api/projects/{slug}/tickets/{numbers[0]}/pin")).EnsureSuccessStatusCode();
        (await responder.PutAsync($"/api/projects/{slug}/tickets/{numbers[3]}/pin", null)).EnsureSuccessStatusCode();

        // Pinned lên đầu danh sách.
        var list = await ReadJsonAsync(await responder.GetAsync($"/api/projects/{slug}/tickets"));
        var first = list.GetProperty("items")[0];
        Assert.True(first.GetProperty("pinned").GetBoolean());
    }

    [Fact]
    public async Task Transfer_gives_new_number_and_old_url_redirects_301()
    {
        var responder = await _fx.ResponderAsync();
        var target = "ops-" + Guid.NewGuid().ToString("N")[..8];
        await EnsureProjectAsync(_fx.Factory, target, "Ops");

        var t = await CreateTicketAsync(responder, "transfer me", extra: new { labels = new[] { "bug" } });
        var oldNumber = I(t, "number");

        var moved = await ReadJsonAsync(await responder.PostAsJsonAsync($"/api/projects/support/tickets/{oldNumber}/transfer", new { toProject = target }, ApiFactory.Json));
        Assert.Equal(target, S(moved, "projectSlug"));
        Assert.Equal(1, I(moved, "number"));
        // Project đích không có label "bug" (project mới không seed) → label bị gỡ, không sinh UNLABELED.
        Assert.Empty(moved.GetProperty("labels").EnumerateArray());

        var handler = _fx.Factory.Server.CreateHandler();
        using var noRedirect = new HttpClient(handler) { BaseAddress = _fx.Factory.Server.BaseAddress };
        noRedirect.DefaultRequestHeaders.Authorization = responder.DefaultRequestHeaders.Authorization;
        var old = await noRedirect.GetAsync($"/api/projects/support/tickets/{oldNumber}");
        Assert.Equal(HttpStatusCode.MovedPermanently, old.StatusCode);
        Assert.Equal($"/api/projects/{target}/tickets/1", old.Headers.Location?.ToString());

        var timeline = await TimelineAsync(responder, 1, project: target);
        Assert.Contains(timeline, e => S(e, "eventType") == "TRANSFERRED");
    }

    [Fact]
    public async Task Comment_edit_history_hide_and_delete_follow_BR_EDIT_01()
    {
        var customer = await _fx.CustomerAsync();
        var responder = await _fx.ResponderAsync();
        var support = await _fx.SupportAsync();
        var t = await CreateTicketAsync(customer, "comment moderation");
        var n = I(t, "number");

        var comment = await CommentAsync(customer, n, "bản 1");
        var id = S(comment, "id");

        // Tác giả sửa 2 lần → 3 bản trong lịch sử, bản cuối là hiện tại.
        (await customer.PatchAsJsonAsync($"/api/projects/support/tickets/{n}/comments/{id}", new { body = "bản 2" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        var edited = await ReadJsonAsync(await customer.PatchAsJsonAsync($"/api/projects/support/tickets/{n}/comments/{id}", new { body = "bản 3" }, ApiFactory.Json));
        Assert.Equal("bản 3", S(edited, "body"));
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);

        var edits = await ReadJsonAsync(await support.GetAsync($"/api/projects/support/tickets/{n}/comments/{id}/edits"));
        Assert.Equal(3, edits.GetArrayLength());
        Assert.True(edits[2].GetProperty("isCurrent").GetBoolean());

        // Support (Triage) không sửa được comment người khác; Responder (Write) ẩn được.
        var forbidden = await support.PatchAsJsonAsync($"/api/projects/support/tickets/{n}/comments/{id}", new { body = "x" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var hidden = await ReadJsonAsync(await responder.PutAsJsonAsync($"/api/projects/support/tickets/{n}/comments/{id}/minimize", new { reason = "off_topic" }, ApiFactory.Json));
        Assert.True(hidden.GetProperty("isHidden").GetBoolean());
        Assert.Equal("OFF_TOPIC", S(hidden, "hiddenReason"));

        var timeline = await TimelineAsync(customer, n);
        var row = Assert.Single(timeline, e => S(e, "id") == id);
        Assert.True(row.GetProperty("isEdited").GetBoolean());
        Assert.True(row.GetProperty("isHidden").GetBoolean());

        // Xoá → biến mất khỏi timeline, comments_count giảm, Event Store vẫn còn (kiểm qua DB).
        (await customer.DeleteAsync($"/api/projects/support/tickets/{n}/comments/{id}")).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await TimelineAsync(customer, n), e => S(e, "id") == id);
        Assert.Equal(0, I(await GetTicketAsync(customer, n), "comments"));

        await using var db = _fx.Factory.CreateDbContext();
        Assert.True(db.TicketEvents.Any(e => e.Id == Guid.Parse(id)));
    }

    [Fact]
    public async Task Permission_matrix_read_triage_write_admin()
    {
        var customer = await _fx.CustomerAsync();
        var other = await _fx.OtherCustomerAsync();
        var support = await _fx.SupportAsync();
        var admin = await _fx.AdminAsync();
        var t = await CreateTicketAsync(customer, "matrix");
        var n = I(t, "number");

        // Read: tác giả sửa tiêu đề của mình; người khác mức Read thì không.
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(customer, n, new { title = "matrix 2" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(other, n, new { title = "hijack" })).StatusCode);

        // Read không gắn label; Triage gắn được (label mặc định "bug").
        Assert.Equal(HttpStatusCode.Forbidden, (await PatchAsync(customer, n, new { labels = new[] { "bug" } })).StatusCode);
        var labeled = await ReadJsonAsync(await PatchAsync(support, n, new { labels = new[] { "bug", "question" } }));
        Assert.Equal(2, labeled.GetProperty("labels").GetArrayLength());

        // Label không tồn tại → 422.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PatchAsync(support, n, new { labels = new[] { "khong-ton-tai" } })).StatusCode);

        // Xoá: Triage 403, Admin 204, sau đó 404.
        Assert.Equal(HttpStatusCode.Forbidden, (await support.DeleteAsync($"/api/projects/support/tickets/{n}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/projects/support/tickets/{n}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await support.GetAsync($"/api/projects/support/tickets/{n}")).StatusCode);
    }

    [Fact]
    public async Task List_filters_state_labels_and_paginates_with_cursor()
    {
        var support = await _fx.SupportAsync();
        var slug = "list-" + Guid.NewGuid().ToString("N")[..8];
        var projectId = await EnsureProjectAsync(_fx.Factory, slug, "List project");
        await using (var db = _fx.Factory.CreateDbContext())
        {
            await IncidentTracker.Api.Persistence.TicketSeeder.EnsureDefaultLabelsAsync(db, projectId, CancellationToken.None);
        }

        for (var i = 0; i < 5; i++)
        {
            await CreateTicketAsync(support, $"list {i}", project: slug, extra: new { labels = i % 2 == 0 ? new[] { "bug" } : Array.Empty<string>() });
        }
        (await PatchAsync(support, 1, new { state = "closed" }, project: slug)).EnsureSuccessStatusCode();

        var open = await ReadJsonAsync(await support.GetAsync($"/api/projects/{slug}/tickets?per_page=2"));
        Assert.Equal(2, open.GetProperty("items").GetArrayLength());
        Assert.Equal(4, open.GetProperty("totalCount").GetInt32());
        var next = open.GetProperty("nextCursor").GetString();
        Assert.NotNull(next);

        var page2 = await ReadJsonAsync(await support.GetAsync($"/api/projects/{slug}/tickets?per_page=2&cursor={next}"));
        Assert.Equal(2, page2.GetProperty("items").GetArrayLength());
        var firstPageNumbers = open.GetProperty("items").EnumerateArray().Select(e => I(e, "number")).ToList();
        var secondPageNumbers = page2.GetProperty("items").EnumerateArray().Select(e => I(e, "number")).ToList();
        Assert.Empty(firstPageNumbers.Intersect(secondPageNumbers));

        var bugs = await ReadJsonAsync(await support.GetAsync($"/api/projects/{slug}/tickets?state=all&labels=bug"));
        Assert.Equal(3, bugs.GetProperty("totalCount").GetInt32());

        var closed = await ReadJsonAsync(await support.GetAsync($"/api/projects/{slug}/tickets?state=closed"));
        Assert.Equal(1, closed.GetProperty("totalCount").GetInt32());
    }
}
