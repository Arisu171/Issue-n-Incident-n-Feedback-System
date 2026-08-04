using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Persistence;

/// <summary>
/// Seed dữ liệu tham chiếu của module Tickets (Architecture v3.1): project mặc định, bộ 9 label
/// mặc định của GitHub (BR-ORG-04), Issue Type mặc định (BR-ORG-06), SLA policy theo priority
/// (UC-16). Idempotent — chạy lại không tạo trùng.
/// </summary>
public static class TicketSeeder
{
    public const string DefaultProjectSlug = "support";

    /// <summary>Đúng 9 label mặc định khi tạo repository mới trên GitHub, kể cả màu.</summary>
    public static readonly (string Name, string Color, string Description)[] DefaultLabels =
    {
        ("bug", "d73a4a", "Something isn't working"),
        ("documentation", "0075ca", "Improvements or additions to documentation"),
        ("duplicate", "cfd3d7", "This issue or pull request already exists"),
        ("enhancement", "a2eeef", "New feature or request"),
        ("good first issue", "7057ff", "Good for newcomers"),
        ("help wanted", "008672", "Extra attention is needed"),
        ("invalid", "e4e669", "This doesn't seem right"),
        ("question", "d876e3", "Further information is requested"),
        ("wontfix", "ffffff", "This will not be worked on")
    };

    public static readonly (string Name, string Color, string Description)[] DefaultIssueTypes =
    {
        ("Bug", "red", "An unexpected problem or behavior"),
        ("Feature", "blue", "A request, idea, or new functionality"),
        ("Task", "yellow", "A specific piece of work"),
        ("Incident", "orange", "Sự cố vận hành cần phản hồi theo SLA (mở rộng riêng)")
    };

    public static readonly (TicketPriority Priority, int Response, int Resolution, int Escalate)[] DefaultSlaPolicies =
    {
        (TicketPriority.P0, 15, 240, 15),
        (TicketPriority.P1, 60, 480, 60),
        (TicketPriority.P2, 240, 2880, 240),
        (TicketPriority.P3, 1440, 10080, 1440)
    };

    public static async Task SeedAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == DefaultProjectSlug, ct);
        if (project is null)
        {
            project = new Project
            {
                Id = Guid.NewGuid(),
                Slug = DefaultProjectSlug,
                Name = "Support Desk",
                Description = "Project mặc định — ticket hỗ trợ và sự cố.",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Đã seed project mặc định '{Slug}'.", DefaultProjectSlug);
        }

        await OpenToSelfServiceRolesAsync(db, project.Id, logger, ct);

        await EnsureDefaultLabelsAsync(db, project.Id, ct);

        var existingTypes = await db.IssueTypes.Select(t => t.Name).ToListAsync(ct);
        var position = 0;
        foreach (var (name, color, description) in DefaultIssueTypes)
        {
            if (!existingTypes.Contains(name))
            {
                db.IssueTypes.Add(new IssueType
                {
                    Id = Guid.NewGuid(), Name = name, Color = color, Description = description,
                    IsEnabled = true, Position = position
                });
            }
            position++;
        }

        var existingPolicies = await db.SlaPolicies.Select(p => p.Priority).ToListAsync(ct);
        foreach (var (priority, response, resolution, escalate) in DefaultSlaPolicies)
        {
            if (!existingPolicies.Contains(priority))
            {
                db.SlaPolicies.Add(new SlaPolicy
                {
                    Id = Guid.NewGuid(), Priority = priority,
                    ResponseTimeMinutes = response, ResolutionTimeMinutes = resolution,
                    EscalateAfterMinutes = escalate, IsActive = true
                });
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Đã seed Issue Type / SLA policy mặc định.");
        }
    }

    /// <summary>
    /// Mở project mặc định cho các vai trò tự phục vụ.
    ///
    /// Quyền của khách hàng là <b>giao</b> của hai vế: quản trị mở project cho vai trò
    /// (<c>project_role_access</c>) và khách hàng tự nhận project đó
    /// (<c>project_subscriptions</c>). Không có vế thứ nhất thì danh mục tham gia rỗng, và một
    /// bản cài mới rơi vào ngõ cụt: đăng ký xong không có project nào để tham gia, nên không
    /// gửi được sự cố nào.
    ///
    /// <b>Chỉ cấp khi project chưa hề được mở cho vai trò nào</b> — tức là chưa ai từng cấu
    /// hình nó. Đã có dù chỉ một dòng thì để nguyên: cấp lại thứ người vận hành vừa gỡ là phá
    /// hoại. Nhờ điều kiện đó, bản cài cũ nâng cấp lên cũng được vá, không riêng bản cài mới.
    /// </summary>
    private static async Task OpenToSelfServiceRolesAsync(
        AppDbContext db, Guid projectId, ILogger logger, CancellationToken ct)
    {
        if (await db.ProjectRoleAccess.AnyAsync(a => a.ProjectId == projectId, ct))
        {
            return;
        }

        var roleIds = await db.Roles
            .Where(r => Permissions.SelfServiceRoles.Contains(r.Name))
            .Select(r => r.Id)
            .ToListAsync(ct);

        if (roleIds.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var roleId in roleIds)
        {
            db.ProjectRoleAccess.Add(new ProjectRoleAccess
            {
                ProjectId = projectId, RoleId = roleId, AddedAt = now
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Đã mở project '{Slug}' cho {Count} vai trò tự phục vụ.", DefaultProjectSlug, roleIds.Count);
    }

    /// <summary>BR-ORG-04 — dùng khi tạo project mới cũng như khi seed.</summary>
    public static async Task EnsureDefaultLabelsAsync(AppDbContext db, Guid projectId, CancellationToken ct)
    {
        var existing = await db.Labels
            .Where(l => l.ProjectId == projectId)
            .Select(l => l.Name.ToLower())
            .ToListAsync(ct);

        foreach (var (name, color, description) in DefaultLabels)
        {
            if (!existing.Contains(name))
            {
                db.Labels.Add(new Label
                {
                    Id = Guid.NewGuid(), ProjectId = projectId, Name = name, ColorHex = color,
                    Description = description, IsDefault = true
                });
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
