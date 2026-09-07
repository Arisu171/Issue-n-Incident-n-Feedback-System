using System.Net.Http.Json;
using System.Text.Json;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>Helper gọi API module Tickets trong test: luôn gửi Idempotency-Key cho POST.</summary>
public static class TicketApi
{
    public const string Project = "support";

    public static HttpRequestMessage Post(string path, object body, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: ApiFactory.Json)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString());
        return request;
    }

    public static async Task<JsonElement> CreateTicketAsync(HttpClient client, string title, string? body = null,
        string project = Project, object? extra = null)
    {
        var payload = new Dictionary<string, object?> { ["title"] = title, ["body"] = body };
        if (extra is not null)
        {
            foreach (var p in JsonSerializer.SerializeToElement(extra, ApiFactory.Json).EnumerateObject())
            {
                payload[p.Name] = p.Value;
            }
        }
        var response = await client.SendAsync(Post($"/api/projects/{project}/tickets", payload));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"create ticket: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<HttpResponseMessage> PatchAsync(HttpClient client, int number, object body, string? ifMatch = null, string project = Project)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/projects/{project}/tickets/{number}")
        {
            Content = JsonContent.Create(body, options: ApiFactory.Json)
        };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        return await client.SendAsync(request);
    }

    public static async Task<JsonElement> GetTicketAsync(HttpClient client, int number, string project = Project)
    {
        var response = await client.GetAsync($"/api/projects/{project}/tickets/{number}");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    public static async Task<JsonElement> CommentAsync(HttpClient client, int number, string body, string project = Project)
    {
        var response = await client.SendAsync(Post($"/api/projects/{project}/tickets/{number}/comments", new { body }));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, $"comment: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<List<JsonElement>> TimelineAsync(HttpClient client, int number, string project = Project)
    {
        var response = await client.GetAsync($"/api/projects/{project}/tickets/{number}/timeline?per_page=100");
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static string S(JsonElement e, string name) => e.GetProperty(name).GetString() ?? string.Empty;
    public static int I(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    /// <summary>
    /// Tạo project thẳng trong database (nhanh hơn đi qua API) — và cấp luôn quyền cho dàn tài
    /// khoản chuẩn.
    ///
    /// Bước cấp quyền là bắt buộc từ khi quyền gắn theo project: một project không có thành viên
    /// nào thì mọi endpoint dưới nó trả 403, kể cả với tài khoản mang vai trò `responder`.
    /// </summary>
    public static async Task<Guid> EnsureProjectAsync(ApiFactory factory, string slug, string name)
    {
        await using var db = factory.CreateDbContext();
        var existing = db.Projects.FirstOrDefault(p => p.Slug == slug);
        if (existing is not null) return existing.Id;
        var project = new Project { Id = Guid.NewGuid(), Slug = slug, Name = name, CreatedAt = DateTimeOffset.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        await GrantStandardAccessAsync(db, project.Id);
        return project.Id;
    }

    /// <summary>Cấp quyền project cho dàn tài khoản chuẩn, viết thẳng vào bảng.</summary>
    public static async Task GrantStandardAccessAsync(AppDbContext db, Guid projectId)
    {
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id);
        var now = DateTimeOffset.UtcNow;

        foreach (var (login, roleName) in new[]
                 {
                     ("support", "support"),
                     ("responder", "responder"),
                     ("mitigator", "mitigator"),
                     ("readonly", "readonly-test"),
                 })
        {
            var userId = await db.Users.Where(u => u.Login == login).Select(u => u.Id).FirstOrDefaultAsync();
            if (userId == Guid.Empty || !roles.TryGetValue(roleName, out var roleId)) continue;
            if (await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == userId && m.RoleId == roleId)) continue;
            db.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = projectId, UserId = userId, RoleId = roleId, AddedAt = now
            });
        }

        if (roles.TryGetValue("customer", out var customerRole))
        {
            if (!await db.ProjectRoleAccess.AnyAsync(a => a.ProjectId == projectId && a.RoleId == customerRole))
            {
                db.ProjectRoleAccess.Add(new ProjectRoleAccess
                {
                    ProjectId = projectId, RoleId = customerRole, AddedAt = now
                });
            }

            // Cấp cho vai trò mới chỉ **mở** project ra thành danh mục; khách còn phải tự nhận
            // thì mới vào được. Test cần bối cảnh "những khách này đang dùng dịch vụ của project
            // này", nên dựng nốt vế thứ hai — cho mọi tài khoản đang mang vai trò customer.
            var customers = await db.UserRoles.Where(ur => ur.RoleId == customerRole)
                .Select(ur => ur.UserId).ToListAsync();
            var joined = await db.ProjectSubscriptions.Where(sub => sub.ProjectId == projectId)
                .Select(sub => sub.UserId).ToListAsync();

            foreach (var userId in customers.Except(joined))
            {
                db.ProjectSubscriptions.Add(new ProjectSubscription
                {
                    ProjectId = projectId, UserId = userId, JoinedAt = now
                });
            }
        }

        await db.SaveChangesAsync();
    }
}
