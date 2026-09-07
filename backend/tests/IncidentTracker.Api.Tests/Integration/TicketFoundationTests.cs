using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// modify_01 — nền tảng dữ liệu của Architecture v3.1: migration chạy được từ DB trống (kể cả
/// SQL partition), seed project/label/type/SLA, users.login được sinh cho mọi tài khoản.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TicketFoundationTests
{
    private readonly ApiFixture _fx;

    public TicketFoundationTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Migration_creates_partitioned_event_store()
    {
        await using var db = _fx.Factory.CreateDbContext();
        await using var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await conn.OpenAsync();

        // Bảng cha là partitioned (relkind = 'p') và có partition DEFAULT + tháng hiện tại.
        await using var cmd = new NpgsqlCommand("""
            select (select relkind from pg_class where relname = 'ticket_events')::text,
                   (select count(*) from pg_inherits i join pg_class c on c.oid = i.inhrelid
                     join pg_class p on p.oid = i.inhparent where p.relname = 'ticket_events')
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("p", reader.GetString(0));
        Assert.True(reader.GetInt64(1) >= 3, "cần partition default + tháng hiện tại + tháng kế");
    }

    [Fact]
    public async Task Seed_creates_default_project_labels_types_and_sla()
    {
        await using var db = _fx.Factory.CreateDbContext();

        var project = await db.Projects.SingleAsync(p => p.Slug == TicketSeeder.DefaultProjectSlug);
        Assert.False(project.StrictClosePolicy);
        Assert.False(project.AutoReopenOnCustomerComment);
        Assert.Equal(1, project.NextTicketNumber);

        var labels = await db.Labels.Where(l => l.ProjectId == project.Id).Select(l => l.Name).ToListAsync();
        Assert.Equal(9, labels.Count);
        Assert.Contains("bug", labels);
        Assert.Contains("good first issue", labels);

        Assert.Equal(4, await db.IssueTypes.CountAsync());
        Assert.Equal(4, await db.SlaPolicies.CountAsync());
    }

    [Fact]
    public async Task Every_user_has_unique_login_derived_from_email()
    {
        await using var db = _fx.Factory.CreateDbContext();
        var users = await db.Users.Select(u => new { u.Email, u.Login }).ToListAsync();

        Assert.NotEmpty(users);
        Assert.All(users, u => Assert.Matches("^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,38}$", u.Login));
        Assert.Equal(users.Count, users.Select(u => u.Login).Distinct().Count());
        Assert.Equal("admin", users.Single(u => u.Email == ApiFixture.AdminEmail).Login);
    }

    [Fact]
    public async Task Ticket_permissions_are_seeded_into_default_roles()
    {
        await using var db = _fx.Factory.CreateDbContext();

        var customer = await db.Roles.Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .SingleAsync(r => r.Name == "customer");
        var codes = customer.RolePermissions.Select(rp => rp.Permission.Code).ToList();
        Assert.Contains("ticket.read", codes);
        Assert.Contains("ticket.create", codes);
        Assert.DoesNotContain("ticket.triage", codes);
        Assert.DoesNotContain("ticket.internal_note", codes);

        var responder = await db.Roles.Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .SingleAsync(r => r.Name == "responder");
        Assert.Contains("ticket.write", responder.RolePermissions.Select(rp => rp.Permission.Code));
    }
}
