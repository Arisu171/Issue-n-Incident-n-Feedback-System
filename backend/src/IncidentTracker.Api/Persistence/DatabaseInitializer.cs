using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Persistence;

/// <summary>Cấu hình seed đọc từ biến môi trường (mục 5.7 — không hard-code mật khẩu).</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>Chạy migration khi khởi động — bật ở dev/compose, tắt ở production.</summary>
    public bool ApplyMigrations { get; set; } = true;

    /// <summary>Email của tài khoản quản trị bootstrap.</summary>
    public string AdminEmail { get; set; } = "admin@incident.local";

    /// <summary>
    /// Mật khẩu admin bootstrap. Bắt buộc cấu hình qua <c>SEED__ADMINPASSWORD</c>;
    /// bỏ trống thì hệ thống bỏ qua bước tạo admin thay vì dùng giá trị mặc định.
    /// </summary>
    public string? AdminPassword { get; set; }

    public string AdminDisplayName { get; set; } = "Quản trị hệ thống";

    /// <summary>
    /// Sinh dữ liệu trình diễn (người dùng, project, ticket, sự cố, phản hồi). Chỉ bật ở môi
    /// trường demo hoặc dev — dữ liệu giả không có chỗ trong một database thật.
    /// </summary>
    public bool DemoData { get; set; }
}

/// <summary>
/// Seed dữ liệu tham chiếu: danh mục permission và role mặc định của bảng 9.2 — nguồn duy
/// nhất là <see cref="Permissions"/>, không lặp lại con số ở đây để khỏi lệch khi danh mục đổi.
/// Idempotent — chạy lại nhiều lần không sinh bản ghi trùng.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SeedOptions>>().Value;
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitializer");

        if (options.ApplyMigrations)
        {
            logger.LogInformation("Áp dụng EF Core migration...");
            await db.Database.MigrateAsync(ct);
        }

        await SeedPermissionsAsync(db, logger, ct);
        await SeedRolesAsync(db, logger, ct);
        // Chạy sau SeedRoles để vai trò thay thế chắc chắn đã tồn tại.
        await RetireRolesAsync(db, logger, ct);
        await SeedAdminAsync(db, hasher, options, logger, ct);

        // Architecture v3.1 — project mặc định, 9 label GitHub, Issue Type, SLA policy.
        await TicketSeeder.SeedAsync(db, logger, ct);

        // Dữ liệu trình diễn đi qua Application Service nên phải chạy sau cùng, khi mọi dữ
        // liệu tham chiếu đã sẵn sàng, và dùng scope riêng của chính nó.
        if (options.DemoData)
        {
            await DemoDataSeeder.SeedAsync(services, logger, ct);
        }
    }

    private static async Task SeedPermissionsAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var existing = await db.Permissions.Select(p => p.Code).ToListAsync(ct);
        var missing = Permissions.Catalog.Keys.Except(existing).ToList();

        if (missing.Count == 0)
        {
            return;
        }

        db.Permissions.AddRange(missing.Select(code => new Permission
        {
            Id = Guid.NewGuid(),
            Code = code,
            Description = Permissions.Catalog[code]
        }));
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Đã seed {Count} permission mới: {Codes}", missing.Count, string.Join(", ", missing));
    }

    private static async Task SeedRolesAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var permissionIds = await db.Permissions.ToDictionaryAsync(p => p.Code, p => p.Id, ct);

        foreach (var (name, (description, rank, codes)) in Permissions.DefaultRoles)
        {
            var role = await db.Roles
                .Include(r => r.RolePermissions)
                .FirstOrDefaultAsync(r => r.Name == name, ct);

            // Chỉ `admin` và `system` có hiệu lực toàn hệ thống; mọi vai trò khác chỉ có quyền ở
            // project được cấp.
            var isGlobal = name is Permissions.AdminRole or Permissions.SystemRole;

            if (role is null)
            {
                role = new Role
                {
                    Id = Guid.NewGuid(), Name = name, Description = description, Rank = rank, IsGlobal = isGlobal
                };
                db.Roles.Add(role);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Đã seed role '{Role}' ở cấp {Rank}.", name, rank);
            }
            else if (role.Rank != rank || role.IsGlobal != isGlobal)
            {
                // Nâng cấp từ bản chưa có thang cấp hoặc chưa có phạm vi: đưa role dựng sẵn về đúng bậc.
                role.Rank = rank;
                role.IsGlobal = isGlobal;
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Đã đặt lại cấp/phạm vi của role '{Role}' thành {Rank}/{Global}.",
                    name, rank, isGlobal);
            }

            var current = role.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();
            var toAdd = codes
                .Where(c => permissionIds.ContainsKey(c))
                .Select(c => permissionIds[c])
                .Where(id => !current.Contains(id))
                .ToList();

            if (toAdd.Count > 0)
            {
                db.RolePermissions.AddRange(toAdd.Select(id => new RolePermission
                {
                    RoleId = role.Id,
                    PermissionId = id
                }));
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Đã gán thêm {Count} permission cho role '{Role}'.", toAdd.Count, name);
            }
        }
    }

    /// <summary>
    /// Gỡ các vai trò đã bỏ khỏi database đang chạy.
    ///
    /// Bỏ một vai trò khỏi <see cref="Permissions.DefaultRoles"/> **không** xoá nó khỏi cơ sở dữ
    /// liệu: <see cref="SeedRolesAsync"/> chỉ thêm, không bao giờ gỡ. Nếu chỉ xoá ở mã thì trên
    /// database đang chạy vai trò vẫn còn, vẫn gán được, và người đang mang nó vẫn giữ quyền —
    /// tức là đã bỏ mà chưa bỏ.
    ///
    /// Danh sách lấy từ <see cref="Permissions.RetiredRoles"/>, cùng danh sách mà
    /// <c>RbacService.CreateRoleAsync</c> dùng để chặn tạo lại — nếu không thì bước gỡ này sẽ ăn
    /// mất một vai trò do người thật tạo ra.
    /// </summary>
    private static async Task RetireRolesAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        foreach (var (retiredName, replacementName) in Permissions.RetiredRoles)
        {
            var retired = await db.Roles.FirstOrDefaultAsync(r => r.Name == retiredName, ct);
            if (retired is null) continue;

            var replacement = await db.Roles.FirstOrDefaultAsync(r => r.Name == replacementName, ct);
            if (replacement is null)
            {
                // Thà để nguyên còn hơn gỡ vai trò của người ta rồi không cấp lại gì: tài khoản
                // không có vai trò nào thì đăng nhập được mà không làm được gì, và không màn hình
                // nào nói vì sao.
                logger.LogError("Không gỡ được role '{Retired}': thiếu role thay thế '{Replacement}'.",
                    retiredName, replacementName);
                continue;
            }

            var links = await db.UserRoles.Where(ur => ur.RoleId == retired.Id).ToListAsync(ct);
            var alreadyHave = (await db.UserRoles
                .Where(ur => ur.RoleId == replacement.Id)
                .Select(ur => ur.UserId)
                .ToListAsync(ct)).ToHashSet();

            foreach (var link in links)
            {
                if (alreadyHave.Add(link.UserId))
                {
                    db.UserRoles.Add(new UserRole { UserId = link.UserId, RoleId = replacement.Id });
                }
            }

            db.UserRoles.RemoveRange(links);
            db.RolePermissions.RemoveRange(await db.RolePermissions.Where(rp => rp.RoleId == retired.Id).ToListAsync(ct));
            db.Roles.Remove(retired);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Đã gỡ role '{Retired}'; {Count} tài khoản chuyển sang '{Replacement}'.",
                retiredName, links.Count, replacementName);
        }
    }

    private static async Task SeedAdminAsync(AppDbContext db, IPasswordHasher<User> hasher,
        SeedOptions options, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.AdminPassword))
        {
            logger.LogWarning(
                "Bỏ qua seed admin vì Seed:AdminPassword chưa được cấu hình. "
                + "Đặt SEED__ADMINPASSWORD trong .env để tạo tài khoản quản trị đầu tiên.");
            return;
        }

        var email = IdentityService.NormalizeEmail(options.AdminEmail);
        var adminRole = await db.Roles.FirstAsync(r => r.Name == Permissions.AdminRole, ct);

        // Tài khoản bootstrap mang cả `system`: nó là lối thoát hiểm duy nhất, mà cấu hình nền
        // (SLA, loại issue) chỉ `system` chạm được. Thiếu vai trò này thì mất quyền cấu hình nền
        // là không còn đường nào lấy lại.
        var systemRole = await db.Roles.FirstAsync(r => r.Name == Permissions.SystemRole, ct);

        var existing = await db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        if (existing is not null)
        {
            // Tài khoản bootstrap là lối thoát hiểm duy nhất: mất role admin hoặc bị khoá thì
            // không còn ai cấu hình được RBAC, và cũng không còn ai cấp lại quyền cho nó. Khôi
            // phục ở mỗi lần khởi động thay vì bắt người vận hành sửa thẳng database. Chỉ chạy
            // khi SEED__ADMINPASSWORD được cấu hình, nên môi trường production bỏ trống biến
            // này sẽ không bị tự cấp quyền sau lưng.
            var repaired = new List<string>();

            if (existing.UserRoles.All(ur => ur.RoleId != adminRole.Id))
            {
                existing.UserRoles.Add(new UserRole { UserId = existing.Id, RoleId = adminRole.Id });
                repaired.Add("gán lại role admin");
            }

            if (existing.UserRoles.All(ur => ur.RoleId != systemRole.Id))
            {
                existing.UserRoles.Add(new UserRole { UserId = existing.Id, RoleId = systemRole.Id });
                repaired.Add("gán lại role system");
            }

            if (!existing.IsActive)
            {
                existing.IsActive = true;
                repaired.Add("kích hoạt lại tài khoản");
            }

            if (repaired.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                logger.LogWarning(
                    "Tài khoản quản trị bootstrap '{Email}' đã mất quyền; khôi phục: {Actions}.",
                    email, string.Join(", ", repaired));
            }

            return;
        }

        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Login = await LoginNames.EnsureUniqueAsync(db, LoginNames.FromEmail(email), ct),
            DisplayName = options.AdminDisplayName,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        admin.PasswordHash = hasher.HashPassword(admin, options.AdminPassword);
        admin.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = adminRole.Id });
        admin.UserRoles.Add(new UserRole { UserId = admin.Id, RoleId = systemRole.Id });

        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        // Không bao giờ ghi mật khẩu vào log (NFR-SEC-02).
        logger.LogInformation("Đã tạo tài khoản quản trị bootstrap '{Email}'.", email);
    }
}
