using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static IncidentTracker.Api.Tests.Integration.TicketApi;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>modify_04 — UC-07/08/09/17, BR-ORG-01..07, BR-TPL-01 (Architecture v3.1).</summary>
[Collection(ApiCollection.Name)]
public sealed class OrganizationTests
{
    private readonly ApiFixture _fx;

    public OrganizationTests(ApiFixture fx) => _fx = fx;

    private async Task<string> NewProjectAsync(string prefix)
    {
        var admin = await _fx.AdminAsync();
        var slug = prefix + "-" + Guid.NewGuid().ToString("N")[..8];
        var response = await admin.PostAsJsonAsync("/api/projects", new { slug, name = "Project " + prefix }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await _fx.GrantProjectAccessAsync(admin, slug);
        return slug;
    }

    [Fact]
    public async Task New_project_gets_nine_default_labels_and_label_name_is_case_insensitive_unique()
    {
        var responder = await _fx.ResponderAsync();
        var slug = await NewProjectAsync("labels");

        var labels = await ReadJsonAsync(await responder.GetAsync($"/api/projects/{slug}/labels"));
        Assert.Equal(9, labels.GetArrayLength());

        var dup = await responder.PostAsJsonAsync($"/api/projects/{slug}/labels", new { name = "BUG", color = "#ff0000" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, dup.StatusCode);

        var created = await ReadJsonAsync(await responder.PostAsJsonAsync($"/api/projects/{slug}/labels", new { name = " Billing ", color = "#0E8A16", description = "Thanh toán" }, ApiFactory.Json));
        Assert.Equal("billing", S(created, "name"));
        Assert.Equal("0e8a16", S(created, "colorHex"));

        // Support (Triage) không tạo label; Customer không tạo.
        var support = await _fx.SupportAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync($"/api/projects/{slug}/labels", new { name = "x" }, ApiFactory.Json)).StatusCode);
    }

    [Fact]
    public async Task Deleting_label_removes_it_from_tickets_without_UNLABELED_event_but_keeps_history()
    {
        var responder = await _fx.ResponderAsync();
        var slug = await NewProjectAsync("labeldel");
        var t = await CreateTicketAsync(responder, "label history", project: slug, extra: new { labels = new[] { "bug" } });
        var n = I(t, "number");

        (await responder.DeleteAsync($"/api/projects/{slug}/labels/bug")).EnsureSuccessStatusCode();

        var after = await GetTicketAsync(responder, n, slug);
        Assert.Empty(after.GetProperty("labels").EnumerateArray());

        var timeline = await TimelineAsync(responder, n, slug);
        var labeled = Assert.Single(timeline, e => S(e, "eventType") == "LABELED");
        Assert.Equal("bug", labeled.GetProperty("payload").GetProperty("label_name").GetString());
        Assert.DoesNotContain(timeline, e => S(e, "eventType") == "UNLABELED");

        // Label mới cùng tên tạo lại được (label cũ đã archive).
        var recreate = await responder.PostAsJsonAsync($"/api/projects/{slug}/labels", new { name = "bug", color = "d73a4a" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.Created, recreate.StatusCode);
    }

    [Fact]
    public async Task Ticket_label_endpoints_add_replace_and_remove()
    {
        var support = await _fx.SupportAsync();
        var slug = await NewProjectAsync("tlabel");
        var n = I(await CreateTicketAsync(support, "labels api", project: slug), "number");

        var added = await ReadJsonAsync(await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/labels", new { labels = new[] { "bug", "question" } }, ApiFactory.Json));
        Assert.Equal(2, added.GetArrayLength());

        var replaced = await ReadJsonAsync(await support.PutAsJsonAsync($"/api/projects/{slug}/tickets/{n}/labels", new { labels = new[] { "wontfix" } }, ApiFactory.Json));
        Assert.Equal("wontfix", S(replaced[0], "name"));

        var removed = await ReadJsonAsync(await support.DeleteAsync($"/api/projects/{slug}/tickets/{n}/labels/wontfix"));
        Assert.Equal(0, removed.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await support.DeleteAsync($"/api/projects/{slug}/tickets/{n}/labels/wontfix")).StatusCode);
    }

    /// <summary>
    /// Label và milestone nay nằm chung **một màn hình**, nên chúng phải chung một mức quyền: ai
    /// không sửa được label thì cũng không sửa được milestone.
    ///
    /// Chỉ ẩn nút ở giao diện là giấu đường đi chứ không đóng nó — người có mỗi
    /// <c>milestone.write</c> vẫn gọi thẳng API được. Test này chứng minh API cũng từ chối.
    /// </summary>
    [Fact]
    public async Task Sua_milestone_doi_luon_quyen_sua_label()
    {
        var admin = await _fx.AdminAsync();
        var slug = await NewProjectAsync("ms-quyen");

        // Vai trò có milestone.write nhưng **không** có label.write.
        var roleName = "ms-only-" + Guid.NewGuid().ToString("N")[..8];
        var role = await ReadJsonAsync(await admin.PostAsJsonAsync("/api/roles",
            new { name = roleName, description = "Chỉ sửa milestone" }, ApiFactory.Json));
        var permissions = await ReadJsonAsync(await admin.GetAsync("/api/permissions"));
        foreach (var code in new[] { "ticket.read", "milestone.write" })
        {
            var id = S(permissions.EnumerateArray().Single(p => S(p, "code") == code), "id");
            (await admin.PutAsync($"/api/roles/{S(role, "id")}/permissions/{id}", null)).EnsureSuccessStatusCode();
        }

        var email = $"msonly-{Guid.NewGuid():N}"[..22] + "@test.local";
        (await admin.PostAsJsonAsync("/api/users",
            new { email, displayName = "Chỉ milestone", password = ApiFixture.Password, roles = new[] { roleName } },
            ApiFactory.Json)).EnsureSuccessStatusCode();
        (await admin.PutAsync($"/api/projects/{slug}/members/{email.Split('@')[0]}/roles/{roleName}", null))
            .EnsureSuccessStatusCode();

        var client = await _fx.Factory.CreateClientAsAsync(email, ApiFixture.Password);

        // Đọc được, nhưng không tạo được milestone dù đang cầm đúng milestone.write.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{slug}/milestones")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/projects/{slug}/milestones", new { title = "v9.9" }, ApiFactory.Json)).StatusCode);

        // Còn người có đủ cả hai thì vẫn làm bình thường.
        var responder = await _fx.ResponderAsync();
        (await responder.PostAsJsonAsync($"/api/projects/{slug}/milestones",
            new { title = "v1.0" }, ApiFactory.Json)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync(
            $"/api/projects/{slug}/milestones/1", new { state = "closed" }, ApiFactory.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/projects/{slug}/milestones/1")).StatusCode);
    }

    [Fact]
    public async Task Milestone_progress_counts_open_closed_and_delete_detaches_without_event()
    {
        var responder = await _fx.ResponderAsync();
        var slug = await NewProjectAsync("ms");

        var ms = await ReadJsonAsync(await responder.PostAsJsonAsync($"/api/projects/{slug}/milestones", new { title = "v1.0", dueOn = "2026-12-31" }, ApiFactory.Json));
        Assert.Equal(1, I(ms, "number"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await responder.PostAsJsonAsync($"/api/projects/{slug}/milestones", new { title = "v1.0" }, ApiFactory.Json)).StatusCode);

        var a = await CreateTicketAsync(responder, "ms a", project: slug, extra: new { milestone = 1 });
        var b = await CreateTicketAsync(responder, "ms b", project: slug, extra: new { milestone = 1 });
        (await PatchAsync(responder, I(a, "number"), new { state = "closed" }, project: slug)).EnsureSuccessStatusCode();

        var progress = await ReadJsonAsync(await responder.GetAsync($"/api/projects/{slug}/milestones/1"));
        Assert.Equal(1, I(progress, "openCount"));
        Assert.Equal(1, I(progress, "closedCount"));

        // Đóng milestone không đóng ticket (BR-ORG-05).
        var closed = await ReadJsonAsync(await responder.PatchAsJsonAsync($"/api/projects/{slug}/milestones/1", new { state = "closed" }, ApiFactory.Json));
        Assert.Equal("CLOSED", S(closed, "state"));
        Assert.Equal("OPEN", S(await GetTicketAsync(responder, I(b, "number"), slug), "state"));

        (await responder.DeleteAsync($"/api/projects/{slug}/milestones/1")).EnsureSuccessStatusCode();
        var detached = await GetTicketAsync(responder, I(b, "number"), slug);
        Assert.Equal(JsonValueKind.Null, detached.GetProperty("milestone").ValueKind);
        Assert.DoesNotContain(await TimelineAsync(responder, I(b, "number"), slug), e => S(e, "eventType") == "DEMILESTONED");
    }

    [Fact]
    public async Task Issue_type_is_org_level_one_per_ticket_and_disabled_type_is_kept_on_old_tickets()
    {
        var admin = await _fx.AdminAsync();
        var support = await _fx.SupportAsync();
        var slug = await NewProjectAsync("types");

        var typeName = "Chore-" + Guid.NewGuid().ToString("N")[..6];
        var created = await ReadJsonAsync(await admin.PostAsJsonAsync("/api/issue-types", new { name = typeName, color = "purple" }, ApiFactory.Json));
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync("/api/issue-types", new { name = "nope" }, ApiFactory.Json)).StatusCode);

        var t = await CreateTicketAsync(support, "typed", project: slug, extra: new { type = typeName });
        Assert.Equal(typeName, t.GetProperty("type").GetProperty("name").GetString());

        var retyped = await ReadJsonAsync(await PatchAsync(support, I(t, "number"), new { type = "Bug" }, project: slug));
        Assert.Equal("Bug", retyped.GetProperty("type").GetProperty("name").GetString());
        var timeline = await TimelineAsync(support, I(t, "number"), slug);
        Assert.Equal(2, timeline.Count(e => S(e, "eventType") == "TYPED"));
        Assert.Equal(1, timeline.Count(e => S(e, "eventType") == "UNTYPED"));

        // Tắt type: ticket cũ vẫn giữ, ticket mới không chọn được; xoá khi đang dùng → 409.
        var t2 = await CreateTicketAsync(support, "typed 2", project: slug, extra: new { type = typeName });
        var id = S(created, "id");
        (await admin.PatchAsJsonAsync($"/api/issue-types/{id}", new { name = typeName, isEnabled = false }, ApiFactory.Json)).EnsureSuccessStatusCode();
        Assert.Equal(typeName, (await GetTicketAsync(support, I(t2, "number"), slug)).GetProperty("type").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await PatchAsync(support, I(t, "number"), new { type = typeName }, project: slug)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/issue-types/{id}")).StatusCode);
    }

    [Fact]
    public async Task Assignees_limited_to_ten_and_only_triage_users_can_receive_work()
    {
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();
        var slug = await NewProjectAsync("assign");
        var n = I(await CreateTicketAsync(customer, "assignees", project: slug), "number");

        // Khách hàng không nhận việc được, kể cả tự nhận. Trước đây tự nhận thì được, nhưng khách
        // không mở được khu vực phân loại và không đổi được trạng thái, nên tên họ nằm trong ô
        // Assignees chỉ làm người khác tưởng đã có người xử lý.
        //
        // Hệ quả: mức Read giờ không tự nhận được nữa — `ticket.read` mà không có `ticket.triage`
        // đúng là định nghĩa của mức Read (WebPrimitives.LevelOf), nên đường tự nhận không còn
        // ai đi được. Giao diện cũng đã bỏ đường dẫn "tự nhận" cho nhóm này.
        var self = await customer.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/assignees", new { assignees = new[] { "customer" } }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, self.StatusCode);
        var refused = await ReadJsonAsync(self);
        Assert.Contains("customer", refused.GetProperty("invalid_assignees").EnumerateArray().Select(x => x.GetString()));

        // Mức Read vẫn không gán được cho người khác. Ở đây assignee hợp lệ nên 403 mới là câu
        // trả lời đúng — nếu ra 422 thì nghĩa là hai quy tắc đã bị kiểm sai thứ tự.
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/assignees", new { assignees = new[] { "support" } }, ApiFactory.Json)).StatusCode);

        // Triage: thêm 10 user (tạo thêm user qua admin) → thứ 11 → 422.
        var admin = await _fx.AdminAsync();
        var logins = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            var email = $"asg{i}-{Guid.NewGuid().ToString("N")[..6]}@test.local";
            var r = await admin.PostAsJsonAsync("/api/users", new { email, displayName = "A" + i, password = ApiFixture.Password, roles = new[] { "responder" } }, ApiFactory.Json);
            r.EnsureSuccessStatusCode();
            logins.Add(email.Split('@')[0]);
        }
        // Trước đây khách tự nhận chiếm một chỗ nên 9 người là đủ chạm trần. Giờ khách không
        // nhận được nữa, phải gán đủ 10 rồi mới thử người thứ 11.
        var ten = await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/assignees", new { assignees = logins }, ApiFactory.Json);
        ten.EnsureSuccessStatusCode();
        Assert.Equal(10, (await ReadJsonAsync(ten)).GetProperty("assignees").GetArrayLength());

        var eleventh = await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/assignees", new { assignees = new[] { "responder" } }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, eleventh.StatusCode);

        var removed = await ReadJsonAsync(await support.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{slug}/tickets/{n}/assignees")
        {
            Content = JsonContent.Create(new { assignees = new[] { logins[0] } }, options: ApiFactory.Json)
        }));
        Assert.Equal(9, removed.GetProperty("assignees").GetArrayLength());
    }

    /// <summary>
    /// U2 — mọi đường gán đều đi qua TicketService.ResolveUsersAsync, nên chỉ cần chặn một chỗ.
    /// Test đi qua cả ba đường để nếu ai đó thêm đường thứ tư mà quên gọi hàm chung thì thấy ngay.
    /// </summary>
    [Fact]
    public async Task Khach_hang_khong_gan_lam_assignee_duoc_o_moi_duong()
    {
        var support = await _fx.SupportAsync();
        var slug = await NewProjectAsync("noncust");

        // 1. Lúc tạo ticket.
        var onCreate = await support.SendAsync(Post($"/api/projects/{slug}/tickets",
            new { title = "gán khi tạo", assignees = new[] { "customer" } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onCreate.StatusCode);

        var n = I(await CreateTicketAsync(support, "gán sau", project: slug), "number");

        // 2. Lúc sửa ticket.
        var onPatch = await support.PatchAsJsonAsync($"/api/projects/{slug}/tickets/{n}",
            new { assignees = new[] { "customer" } }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onPatch.StatusCode);

        // 3. Qua endpoint assignees riêng.
        var onAssign = await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/assignees",
            new { assignees = new[] { "customer" } }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onAssign.StatusCode);
        var problem = await ReadJsonAsync(onAssign);
        Assert.Contains("customer", problem.GetProperty("invalid_assignees").EnumerateArray().Select(x => x.GetString()));

        // Người có ticket.triage thì vẫn gán bình thường — chặn nhầm cả nhân viên còn tệ hơn.
        var ok = await support.PostAsJsonAsync($"/api/projects/{slug}/tickets/{n}/assignees",
            new { assignees = new[] { "responder" } }, ApiFactory.Json);
        ok.EnsureSuccessStatusCode();
        Assert.Single((await ReadJsonAsync(ok)).GetProperty("assignees").EnumerateArray());
    }

    /// <summary>
    /// E1 — ô chọn assignee trước đây gọi GET /api/users (đòi user.read, chỉ admin có) trong khi
    /// nút mở ô lại mở theo ticket.triage, nên support và responder bấm vào là 403. Endpoint mới
    /// mở theo đúng ticket.triage và không trả email hay vai trò của ai.
    /// </summary>
    [Fact]
    public async Task Danh_sach_nguoi_gan_duoc_mo_cho_nhom_triage_va_khong_lo_them_gi()
    {
        var slug = await NewProjectAsync("assignable");
        var support = await _fx.SupportAsync();
        var customer = await _fx.CustomerAsync();

        // Chính chỗ trước đây trả 403.
        var res = await support.GetAsync($"/api/projects/{slug}/assignable-users");
        res.EnsureSuccessStatusCode();
        var list = await ReadJsonAsync(res);

        var logins = list.EnumerateArray().Select(u => S(u, "login")).ToList();
        Assert.Contains("support", logins);
        Assert.Contains("responder", logins);
        Assert.DoesNotContain("customer", logins);

        // Chỉ id, login, displayName — không email, không role, không isActive.
        var first = list.EnumerateArray().First();
        Assert.Equal(3, first.EnumerateObject().Count());
        Assert.False(first.TryGetProperty("email", out _));
        Assert.False(first.TryGetProperty("roles", out _));

        // Khách hàng không có ticket.triage nên không xem được danh sách nhân viên.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await customer.GetAsync($"/api/projects/{slug}/assignable-users")).StatusCode);

        // Lọc theo từ khoá, và project không tồn tại thì 404 chứ không trả danh sách rỗng.
        var filtered = await ReadJsonAsync(await support.GetAsync($"/api/projects/{slug}/assignable-users?search=respon"));
        Assert.All(filtered.EnumerateArray(), u => Assert.Contains("respon", S(u, "login") + S(u, "displayName").ToLowerInvariant()));
        // 403 chứ không còn 404: kiểm quyền theo project chạy **trước** controller, và không ai
        // có quyền trên một project không tồn tại. Đây là cải thiện — phản hồi không còn cho biết
        // project nào có thật, tức mất một đường dò tên project.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await support.GetAsync("/api/projects/khong-co-project-nay/assignable-users")).StatusCode);
    }

    [Fact]
    public async Task Issue_form_validates_required_fields_renders_markdown_and_applies_defaults()
    {
        var responder = await _fx.ResponderAsync();
        var customer = await _fx.CustomerAsync();
        var admin = await _fx.AdminAsync();
        var slug = await NewProjectAsync("form");

        var body = JsonSerializer.Deserialize<JsonElement>("""
        [
          {"type":"markdown","attributes":{"value":"Cảm ơn bạn"}},
          {"type":"input","id":"contact","attributes":{"label":"Email liên hệ"}},
          {"type":"textarea","id":"what","attributes":{"label":"Điều gì đã xảy ra?","render":"shell"},"validations":{"required":true}},
          {"type":"dropdown","id":"version","attributes":{"label":"Phiên bản","options":["1.0","2.0"]},"validations":{"required":true}},
          {"type":"checkboxes","id":"terms","attributes":{"label":"Xác nhận","options":[{"label":"Đã tìm ticket trùng","required":true}]}}
        ]
        """);
        var template = await ReadJsonAsync(await responder.PostAsJsonAsync($"/api/projects/{slug}/templates",
            new { name = "Bug report", title = "[Bug]: ", labels = new[] { "bug" }, type = "Bug", body }, ApiFactory.Json));
        var templateId = S(template, "id");

        // Schema sai → 400.
        var bad = await responder.PostAsJsonAsync($"/api/projects/{slug}/templates", new { name = "bad", body = JsonSerializer.Deserialize<JsonElement>("""[{"type":"dropdown","id":"x","attributes":{"label":"x","options":[]}}]""") }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Thiếu required → 422 kèm errors[].
        var missing = await customer.SendAsync(Post($"/api/projects/{slug}/tickets", new { title = "Lỗi đăng nhập", templateId, formAnswers = new { contact = "a@b.c" } }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        var problem = await ReadJsonAsync(missing);
        Assert.True(problem.GetProperty("errors").GetArrayLength() >= 2);

        var ok = await customer.SendAsync(Post($"/api/projects/{slug}/tickets", new
        {
            title = "Lỗi đăng nhập",
            templateId,
            formAnswers = new { what = "bấm login thì trắng trang", version = "2.0", terms = new[] { "Đã tìm ticket trùng" } }
        }));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var ticket = await ReadJsonAsync(ok);
        Assert.Equal("[Bug]: Lỗi đăng nhập", S(ticket, "title"));
        Assert.Contains("### Điều gì đã xảy ra?", S(ticket, "body"));
        Assert.Contains("```shell\nbấm login thì trắng trang\n```", S(ticket, "body"));
        Assert.Contains("- [x] Đã tìm ticket trùng", S(ticket, "body"));
        Assert.DoesNotContain("Cảm ơn bạn", S(ticket, "body"));
        Assert.Contains("<input", S(ticket, "bodyHtml"));
        // defaults: label/type được gắn dù người tạo mức Read (template là của project).
        Assert.Contains(ticket.GetProperty("labels").EnumerateArray(), l => S(l, "name") == "bug");
        Assert.Equal("Bug", ticket.GetProperty("type").GetProperty("name").GetString());

        // blank_issues_enabled=false → mức Read bắt buộc template.
        (await admin.PatchAsJsonAsync($"/api/projects/{slug}", new { blankIssuesEnabled = false }, ApiFactory.Json)).EnsureSuccessStatusCode();
        var blank = await customer.SendAsync(Post($"/api/projects/{slug}/tickets", new { title = "blank" }));
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    /// <summary>
    /// ValidateSchema cho options ở cả hai dạng — chuỗi trần và object có label — nhưng RenderAsync
    /// từng chỉ chịu được một dạng cho mỗi loại, và là hai dạng ngược nhau. Test cũ dùng đúng cặp
    /// chạy được (dropdown chuỗi, checkboxes object) nên không thấy gì. Đây là cặp còn lại: trước
    /// khi sửa, cả hai đều ném InvalidOperationException, tức 500 lúc có người đi điền biểu mẫu.
    /// Trình soạn template trên giao diện luôn sinh options dạng chuỗi, nên mọi checkboxes tạo từ
    /// đó đều rơi vào đây.
    /// </summary>
    [Fact]
    public async Task Issue_form_nhan_options_dang_chuoi_lan_dang_object()
    {
        var responder = await _fx.ResponderAsync();
        var slug = await NewProjectAsync("optshape");

        var body = JsonSerializer.Deserialize<JsonElement>("""
        [
          {"type":"checkboxes","id":"ack","attributes":{"label":"Xác nhận","options":["Đã đọc quy định","Đã thử lại"]}},
          {"type":"dropdown","id":"sev","attributes":{"label":"Mức độ","options":[{"label":"P1"},{"label":"P2"}]}}
        ]
        """);
        var template = await ReadJsonAsync(await responder.PostAsJsonAsync($"/api/projects/{slug}/templates",
            new { name = "Option shapes", body }, ApiFactory.Json));

        var res = await responder.SendAsync(Post($"/api/projects/{slug}/tickets", new
        {
            title = "options",
            templateId = S(template, "id"),
            formAnswers = new { ack = new[] { "Đã thử lại" }, sev = "P2" }
        }));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var ticket = await ReadJsonAsync(res);
        var md = S(ticket, "body");
        Assert.Contains("- [ ] Đã đọc quy định", md);
        Assert.Contains("- [x] Đã thử lại", md);
        Assert.Contains("### Mức độ\n\nP2", md);
    }

    [Fact]
    public async Task Board_moves_ticket_to_done_when_closed_and_records_column_change()
    {
        var responder = await _fx.ResponderAsync();
        var slug = await NewProjectAsync("board");
        var t = await CreateTicketAsync(responder, "board item", project: slug);
        var n = I(t, "number");

        var board = await ReadJsonAsync(await responder.PostAsJsonAsync("/api/boards", new { name = "Sprint " + Guid.NewGuid().ToString("N")[..6] }, ApiFactory.Json));
        var boardId = S(board, "id");
        var columns = board.GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal(new[] { "Todo", "In Progress", "Done" }, columns.Select(c => S(c, "name")).ToArray());
        var todo = S(columns[0], "id");
        var inProgress = S(columns[1], "id");
        var done = S(columns[2], "id");

        var item = await ReadJsonAsync(await responder.PostAsJsonAsync($"/api/boards/{boardId}/items", new { ticket = $"{slug}#{n}" }, ApiFactory.Json));
        Assert.Equal(todo, S(item, "columnId"));
        Assert.Equal(HttpStatusCode.Conflict, (await responder.PostAsJsonAsync($"/api/boards/{boardId}/items", new { ticket = $"{slug}#{n}" }, ApiFactory.Json)).StatusCode);

        var moved = await ReadJsonAsync(await responder.PatchAsJsonAsync($"/api/boards/{boardId}/items/{S(item, "id")}", new { columnId = inProgress }, ApiFactory.Json));
        Assert.Equal(inProgress, S(moved, "columnId"));

        (await PatchAsync(responder, n, new { state = "closed" }, project: slug)).EnsureSuccessStatusCode();

        // Automation chạy nền qua consumer → chờ tối đa 15s.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        JsonElement detail = default;
        while (DateTime.UtcNow < deadline)
        {
            detail = await ReadJsonAsync(await responder.GetAsync($"/api/boards/{boardId}"));
            if (S(detail.GetProperty("items")[0], "columnId") == done) break;
            await Task.Delay(250);
        }
        Assert.Equal(done, S(detail.GetProperty("items")[0], "columnId"));

        var timeline = await TimelineAsync(responder, n, slug);
        Assert.Equal(2, timeline.Count(e => S(e, "eventType") == "BOARD_COLUMN_CHANGED"));
        Assert.Contains(timeline, e => S(e, "eventType") == "ADDED_TO_BOARD");
    }

    /// <summary>
    /// Hồi quy: thêm cột vào board đã tồn tại từng trả 500. Khoá BoardColumn.Id khai báo là do DB
    /// sinh trong khi mã tự gán Guid, nên EF xếp cột mới (gắn qua navigation của một Board đang
    /// được theo dõi) vào Modified và phát UPDATE 0 dòng thay vì INSERT.
    /// </summary>
    /// <summary>
    /// U8 — xóa cột đòi cột trống.
    ///
    /// Trước đây xóa lúc nào cũng được và thẻ rơi về "Chưa phân cột" nhờ khoá ngoại SetNull.
    /// Không mất dữ liệu nhưng cũng không ai thấy chúng đi đâu. Test khoá cả hai chiều: còn thẻ
    /// thì 409 **và cột phải còn nguyên**, trống rồi thì xóa được.
    /// </summary>
    [Fact]
    public async Task Xoa_cot_doi_cot_trong_truoc()
    {
        var responder = await _fx.ResponderAsync();
        var board = await ReadJsonAsync(await responder.PostAsJsonAsync(
            "/api/boards", new { name = "Empty " + Guid.NewGuid().ToString("N")[..6] }, ApiFactory.Json));
        var boardId = S(board, "id");
        var columns = board.GetProperty("columns").EnumerateArray().ToList();
        var from = S(columns[0], "id");
        var to = S(columns[1], "id");

        var item = await ReadJsonAsync(await responder.PostAsJsonAsync(
            $"/api/boards/{boardId}/items", new { draftTitle = "một thẻ", columnId = from }, ApiFactory.Json));

        var refused = await responder.DeleteAsync($"/api/boards/{boardId}/columns/{from}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await ReadJsonAsync(refused);
        Assert.Equal(1, problem.GetProperty("remaining_items").GetInt32());

        // Cột vẫn còn: 409 mà vẫn xóa mất thì còn tệ hơn không chặn.
        var still = await ReadJsonAsync(await responder.GetAsync($"/api/boards/{boardId}"));
        Assert.Contains(still.GetProperty("board").GetProperty("columns").EnumerateArray(), c => S(c, "id") == from);

        // Chuyển thẻ sang cột khác rồi xóa thì được.
        (await responder.PatchAsJsonAsync($"/api/boards/{boardId}/items/{S(item, "id")}",
            new { columnId = to }, ApiFactory.Json)).EnsureSuccessStatusCode();
        (await responder.DeleteAsync($"/api/boards/{boardId}/columns/{from}")).EnsureSuccessStatusCode();

        var after = await ReadJsonAsync(await responder.GetAsync($"/api/boards/{boardId}"));
        Assert.DoesNotContain(after.GetProperty("board").GetProperty("columns").EnumerateArray(), c => S(c, "id") == from);
        // Thẻ vẫn còn, chỉ đổi cột — xóa cột không được kéo theo thẻ.
        Assert.Single(after.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Them_sua_xoa_cot_tren_board_da_ton_tai()
    {
        var responder = await _fx.ResponderAsync();
        var board = await ReadJsonAsync(await responder.PostAsJsonAsync(
            "/api/boards", new { name = "Cols " + Guid.NewGuid().ToString("N")[..6] }, ApiFactory.Json));
        var boardId = S(board, "id");

        var added = await responder.PostAsJsonAsync($"/api/boards/{boardId}/columns", new { name = "Blocked", position = 2 }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var afterAdd = await ReadJsonAsync(added);
        var blocked = afterAdd.GetProperty("columns").EnumerateArray().Single(c => S(c, "name") == "Blocked");
        Assert.Equal(4, afterAdd.GetProperty("columns").GetArrayLength());

        // Trùng tên bị chặn ở tầng nghiệp vụ, không phải 500.
        var duplicate = await responder.PostAsJsonAsync($"/api/boards/{boardId}/columns", new { name = "blocked" }, ApiFactory.Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.StatusCode);

        var renamed = await ReadJsonAsync(await responder.PatchAsJsonAsync(
            $"/api/boards/{boardId}/columns/{S(blocked, "id")}", new { name = "Waiting", position = 2 }, ApiFactory.Json));
        Assert.Contains(renamed.GetProperty("columns").EnumerateArray(), c => S(c, "name") == "Waiting");

        (await responder.DeleteAsync($"/api/boards/{boardId}/columns/{S(blocked, "id")}")).EnsureSuccessStatusCode();
        var finalBoard = await ReadJsonAsync(await responder.GetAsync($"/api/boards/{boardId}"));
        Assert.DoesNotContain(finalBoard.GetProperty("board").GetProperty("columns").EnumerateArray(), c => S(c, "name") == "Waiting");
    }
}
