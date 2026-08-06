using System.Security.Claims;
using System.Text.Json;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Identity;
using IncidentTracker.Api.Modules.Tickets;
using IncidentTracker.Api.Modules.Tickets.Organization;
using IncidentTracker.Api.Modules.Tickets.Relations;
using IncidentTracker.Api.Modules.Tickets.Social;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Persistence;

/// <summary>
/// Dữ liệu trình diễn, bật bằng <c>SEED__DEMODATA=true</c>. Khác hẳn <see cref="TicketSeeder"/>:
/// đó là dữ liệu tham chiếu bắt buộc của sản phẩm, còn đây là dữ liệu giả để xem giao diện có
/// gì để hiển thị.
///
/// Ticket đi qua đúng các Application Service của sản phẩm chứ không ghi thẳng vào bảng. Chậm
/// hơn nhiều lần nhưng đổi lại timeline, bộ đếm bình luận, chỉ mục tìm kiếm, thông báo và
/// subscription đều do chính mã nghiệp vụ sinh ra — ghi tay sẽ cho một database "trông đúng"
/// mà mọi projection đều sai.
///
/// <b>Quy mô cố định</b>, không còn tham số điều chỉnh:
/// <list type="bullet">
///   <item>2 project trình diễn: <c>demo-alpha</c> và <c>demo-beta</c>;</item>
///   <item>mỗi project 9 issue (5 mở, 4 đóng), 25 sự cố, 25 phản hồi;</item>
///   <item>3 manager, 5 support, 5 responder, 30 khách hàng;</item>
///   <item>mỗi project 2 manager, 4 support, 4 responder — phần giao là người phụ trách cả hai.</item>
/// </list>
///
/// Tài khoản <c>admin</c> và <c>system</c> KHÔNG do đây dựng: admin đến từ
/// <see cref="DatabaseInitializer"/>, system chỉ đến từ cấu hình triển khai.
///
/// Idempotent theo từng khối: mỗi khối tự đếm những gì đã có rồi mới quyết định chạy hay bỏ qua.
/// </summary>
public static class DemoDataSeeder
{
    private const string Password = "Demo#12345";
    private static readonly Random Rng = new(20260909); // Cố định để mỗi lần dựng lại ra cùng dữ liệu.

    private const string Alpha = "demo-alpha";
    private const string Beta = "demo-beta";

    /// <summary>25 sự cố và 25 phản hồi cho mỗi project.</summary>
    private const int IncidentsPerProject = 25;
    private const int FeedbacksPerProject = 25;

    /// <summary>
    /// Một tài khoản trình diễn. <c>Projects</c> là danh sách project người này được cấp quyền —
    /// nguồn duy nhất quyết định bảng <c>project_members</c>, nên đọc mảng này là biết chính xác
    /// ai vào được đâu, không phải suy ra từ đoạn mã cấp quyền.
    /// </summary>
    private sealed record Person(string Login, string Name, string Role, string[] Projects);

    private static readonly string[] Both = { Alpha, Beta };

    private static readonly (string Slug, string Name, string Description)[] Projects =
    {
        (Alpha, "Demo Alpha", "Project trình diễn A — thanh toán, đối soát và hoàn tiền"),
        (Beta, "Demo Beta", "Project trình diễn B — cổng khách hàng và API công khai"),
    };

    /// <summary>
    /// 13 nhân viên: 3 manager, 5 support, 5 responder.
    ///
    /// Phần giao là chủ ý chứ không phải tiện tay: 1 manager, 3 support và 3 responder phụ trách
    /// cả hai project, số còn lại chỉ thuộc một project. Có đủ cả hai kiểu thì màn hình phân
    /// quyền mới cho thấy được điều đáng xem — người phụ trách hai nơi thấy gì khi đổi project,
    /// và người chỉ thuộc một project thì không thấy gì của project kia.
    /// </summary>
    private static readonly Person[] Staff =
    {
        new("bao.long", "Nguyễn Bảo Long", "manager", Both),
        new("minh.anh", "Trần Minh Anh", "manager", new[] { Alpha }),
        new("son.truong", "Lê Sơn Trường", "manager", new[] { Beta }),

        new("huy.kien", "Nguyễn Huy Kiên", "support", Both),
        new("kim.chi", "Hoàng Kim Chi", "support", Both),
        new("gia.bao", "Ngô Gia Bảo", "support", Both),
        new("thanh.tung", "Vũ Thanh Tùng", "support", new[] { Alpha }),
        new("phuong.linh", "Đặng Phương Linh", "support", new[] { Beta }),

        new("thu.ha", "Phạm Thu Hà", "responder", Both),
        new("quang.dung", "Đỗ Quang Dũng", "responder", Both),
        new("hai.dang", "Nguyễn Hải Đăng", "responder", Both),
        new("tuan.anh", "Vũ Tuấn Anh", "responder", new[] { Alpha }),
        new("khanh.chi", "Đỗ Khánh Chi", "responder", new[] { Beta }),
    };

    /// <summary>30 khách hàng, đều tham gia cả hai project.</summary>
    private static readonly Person[] Customers = new[]
    {
        ("tuan.kiet", "Lý Tuấn Kiệt"), ("my.duyen", "Trịnh Mỹ Duyên"), ("hoang.nam", "Bùi Hoàng Nam"),
        ("ngoc.mai", "Phan Ngọc Mai"), ("van.khanh", "Tạ Văn Khánh"), ("hai.yen", "Chu Hải Yến"),
        ("duc.thinh", "Mai Đức Thịnh"), ("bich.ngoc", "Lâm Bích Ngọc"), ("trung.hieu", "Dương Trung Hiếu"),
        ("lan.huong", "Đinh Lan Hương"), ("cong.vinh", "Hồ Công Vinh"), ("thuy.trang", "Nguyễn Thuỳ Trang"),
        ("anh.tu", "Võ Anh Tú"), ("kieu.oanh", "Trần Kiều Oanh"), ("dinh.phong", "Lê Đình Phong"),
        ("thu.hien", "Phạm Thu Hiền"), ("nhat.minh", "Đỗ Nhật Minh"), ("hong.nhung", "Vũ Hồng Nhung"),
        ("xuan.bach", "Hoàng Xuân Bách"), ("quoc.huy", "Nguyễn Quốc Huy"), ("thao.vy", "Trần Thảo Vy"),
        ("minh.tam", "Lê Minh Tâm"), ("ha.linh", "Phạm Hà Linh"), ("tien.dat", "Đỗ Tiến Đạt"),
        ("kim.ngan", "Vũ Kim Ngân"), ("viet.hung", "Hoàng Việt Hưng"), ("ngoc.diep", "Ngô Ngọc Diệp"),
        ("thanh.son", "Đặng Thanh Sơn"), ("mai.phuong", "Lý Mai Phương"), ("dang.khoa", "Bùi Đăng Khoa"),
    }.Select(x => new Person(x.Item1, x.Item2, "customer", Both)).ToArray();

    private static IEnumerable<Person> People => Staff.Concat(Customers);

    /// <summary>
    /// Người đứng tên mọi thao tác cần quyền ghi của seeder: manager phụ trách cả hai project.
    ///
    /// Cố ý KHÔNG dùng admin bootstrap. Admin có thể không tồn tại (thiếu
    /// <c>SEED__ADMINPASSWORD</c> thì bước tạo admin bị bỏ qua), mà dữ liệu trình diễn không
    /// được phép phụ thuộc vào một tài khoản có thể vắng mặt. Manager cũng đã đủ quyền cho toàn
    /// bộ thao tác ở đây — đóng, khoá, ghim ticket và dựng board.
    /// </summary>
    private const string Lead = "bao.long";

    /// <summary>9 label thêm — cộng với 9 label mặc định là 18 mỗi project.</summary>
    private static readonly (string Name, string Color, string Description)[] ExtraLabels =
    {
        ("incident", "ec3013", "Dịch vụ đang suy giảm hoặc chết"),
        ("billing", "bf8700", "Thanh toán và hoá đơn"),
        ("eu-region", "0e8a16", "Chỉ ảnh hưởng khu vực EU"),
        ("integration", "1d76db", "Tích hợp với hệ thống ngoài"),
        ("feedback", "7057ff", "Do khách hàng gửi lên"),
        ("performance", "d876e3", "Chậm hoặc tốn tài nguyên"),
        ("security", "b60205", "Rủi ro bảo mật"),
        ("regression", "e99695", "Trước đây chạy đúng"),
        ("needs-triage", "fbca04", "Chờ phân loại"),
    };

    public static async Task SeedAsync(IServiceProvider root, ILogger logger, CancellationToken ct)
    {
        // Mỗi khối tự chốt idempotent thay vì chốt chung ở đây. Chốt chung có một lỗi thật:
        // thêm khối mới (ví dụ board) thì nó không bao giờ chạy trên database đã seed trước đó.
        logger.LogInformation("Kiểm tra dữ liệu trình diễn...");

        var actors = await SeedFoundationAsync(root, logger, ct);
        await SeedTicketsAsync(root, actors, logger, ct);
        await SeedBoardsAsync(root, actors, logger, ct);
        await SeedLegacyAsync(root, actors, logger, ct);

        logger.LogInformation("Seed dữ liệu trình diễn xong.");
    }

    // ---------------------------------------------------------------- nền

    /// <summary>Người dùng, project, label, milestone. Ghi thẳng vì không có projection nào phụ thuộc.</summary>
    private static async Task<Dictionary<string, Actor>> SeedFoundationAsync(
        IServiceProvider root, ILogger logger, CancellationToken ct)
    {
        await using var scope = root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, ct);
        var actors = new Dictionary<string, Actor>();
        var createdAt = DateTimeOffset.UtcNow.AddDays(-120);
        var newAccounts = 0;

        foreach (var person in People)
        {
            var email = $"{person.Login}@demo.local";
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is null)
            {
                user = new User
                {
                    Id = Guid.NewGuid(),
                    Email = email,
                    Login = await LoginNames.EnsureUniqueAsync(db, person.Login.Replace('.', '-'), ct),
                    DisplayName = person.Name,
                    IsActive = true,
                    CreatedAt = createdAt.AddDays(Rng.Next(0, 60)),
                };
                user.PasswordHash = hasher.HashPassword(user, Password);
                user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = roles[person.Role].Id });
                db.Users.Add(user);
                newAccounts++;
            }
            actors[person.Login] = new Actor(user, person.Role);
        }
        await db.SaveChangesAsync(ct);

        foreach (var (slug, name, description) in Projects)
        {
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Slug == slug, ct);
            if (project is null)
            {
                project = new Project
                {
                    Id = Guid.NewGuid(),
                    Slug = slug,
                    Name = name,
                    Description = description,
                    BlankIssuesEnabled = true,
                    NextTicketNumber = 1,
                    CreatedAt = createdAt,
                    ContactLinks = "[]",
                };
                db.Projects.Add(project);
                await db.SaveChangesAsync(ct);
                await GrantProjectAccessAsync(db, project, actors, createdAt, ct);
            }

            if (await db.Labels.AnyAsync(l => l.ProjectId == project.Id, ct))
            {
                continue; // Label và milestone của project này đã dựng ở lần chạy trước.
            }

            var labels = TicketSeeder.DefaultLabels
                .Select(l => (l.Name, l.Color, l.Description))
                .Concat(ExtraLabels)
                .ToList();
            db.Labels.AddRange(labels.Select(l => new Domain.Label
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Name = l.Name,
                ColorHex = l.Color,
                Description = l.Description,
                IsDefault = false,
            }));

            for (var i = 1; i <= 18; i++)
            {
                var closed = i <= 12;
                db.Milestones.Add(new Milestone
                {
                    Id = Guid.NewGuid(),
                    ProjectId = project.Id,
                    Number = i,
                    Title = $"v{1 + (i - 1) / 6}.{(i - 1) % 6}",
                    Description = $"Đợt phát hành {i} của {name}",
                    DueOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-90 + i * 10)),
                    State = closed ? TicketState.Closed : TicketState.Open,
                    ClosedAt = closed ? createdAt.AddDays(i * 3) : null,
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt,
                });
            }
            await db.SaveChangesAsync(ct);
        }

        await GrantFallbackAccessAsync(db, actors, createdAt, logger, ct);

        logger.LogInformation(
            "Nền trình diễn: thêm {New} tài khoản trên tổng {Total} ({Managers} manager, {Supports} support, "
            + "{Responders} responder, {Customers} khách hàng); {Projects} project, {Labels} label, {Milestones} milestone.",
            newAccounts, People.Count(),
            Staff.Count(p => p.Role == "manager"), Staff.Count(p => p.Role == "support"),
            Staff.Count(p => p.Role == "responder"), Customers.Length,
            Projects.Length, Projects.Length * 18, Projects.Length * 18);
        return actors;
    }

    /// <summary>
    /// Cấp quyền cho đúng những người thuộc project này.
    ///
    /// <b>Vì sao seeder phải làm việc này.</b> Hai bước di trú cấp quyền — thành viên project và
    /// role-access cho khách hàng — nằm trong migration, mà migration chạy <b>trước</b> seeder.
    /// Trên một cơ sở dữ liệu trắng chúng không có project nào để cấp, nên nếu seeder không tự
    /// cấp thì clone về, seed xong, mọi tài khoản trừ admin đều không vào được project nào: đúng
    /// theo thiết kế cách ly, nhưng vô dụng làm dữ liệu trình diễn.
    ///
    /// <b>Chỉ chạy khi project vừa được tạo trong lần chạy này</b>, không chạy mỗi lần khởi động:
    /// cấp lại sau khi người vận hành vừa thu hẹp là phá hoại — cùng lý do đã ghi ở bước di trú.
    /// </summary>
    private static async Task GrantProjectAccessAsync(
        AppDbContext db, Project project, Dictionary<string, Actor> actors,
        DateTimeOffset now, CancellationToken ct)
    {
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id, ct);

        // Nhân viên vào thẳng project với đúng vai trò của mình — và chỉ những người được phân
        // công vào project này, chứ không phải cả dàn nhân sự. Đây là điểm khác căn bản so với
        // bản trước: có người ngoài project thì việc cách ly mới có gì để chứng minh.
        foreach (var person in Staff.Where(p => p.Projects.Contains(project.Slug)))
        {
            db.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = project.Id,
                UserId = actors[person.Login].User.Id,
                RoleId = roles[person.Role],
                AddedAt = now,
            });
        }

        // Khách hàng cấp theo vai trò, rồi tự nhận: quyền thật là giao của hai vế, nên thiếu vế
        // thứ hai thì dữ liệu demo vẫn trống với họ.
        if (roles.TryGetValue("customer", out var customerRole))
        {
            db.ProjectRoleAccess.Add(new ProjectRoleAccess
            {
                ProjectId = project.Id, RoleId = customerRole, AddedAt = now
            });

            foreach (var person in Customers.Where(p => p.Projects.Contains(project.Slug)))
            {
                db.ProjectSubscriptions.Add(new ProjectSubscription
                {
                    ProjectId = project.Id, UserId = actors[person.Login].User.Id, JoinedAt = now
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Project mặc định <c>support</c> do <see cref="TicketSeeder"/> dựng nằm ngoài hai project
    /// trình diễn, nhưng không bỏ trắng được: đó là nơi khách hàng tự đăng ký rơi vào. Không có
    /// nhân viên nào ở đó thì mọi ticket gửi qua đường tự phục vụ đều không ai nhìn thấy.
    ///
    /// Chỉ cấp cho 5 support — đúng vai trò của project này — và chỉ khi chưa ai được cấp, để
    /// lần khởi động sau không dựng lại thứ người vận hành vừa gỡ.
    /// </summary>
    private static async Task GrantFallbackAccessAsync(
        AppDbContext db, Dictionary<string, Actor> actors, DateTimeOffset now, ILogger logger, CancellationToken ct)
    {
        var fallback = await db.Projects
            .Where(p => p.Slug == TicketSeeder.DefaultProjectSlug)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(ct);

        if (fallback == Guid.Empty || await db.ProjectMembers.AnyAsync(m => m.ProjectId == fallback, ct))
        {
            return;
        }

        var supportRole = await db.Roles.Where(r => r.Name == "support").Select(r => r.Id).FirstOrDefaultAsync(ct);
        if (supportRole == Guid.Empty) return;

        foreach (var person in Staff.Where(p => p.Role == "support"))
        {
            db.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = fallback, UserId = actors[person.Login].User.Id, RoleId = supportRole, AddedAt = now
            });
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Đã đưa {Count} support vào project mặc định '{Slug}'.",
            Staff.Count(p => p.Role == "support"), TicketSeeder.DefaultProjectSlug);
    }

    // ------------------------------------------------------------- ticket

    /// <summary>
    /// Kịch bản của một issue.
    ///
    /// <c>Outcome</c> quyết định trạng thái cuối: <c>open</c> / <c>locked</c> / <c>pinned</c> để
    /// mở, <c>closed-*</c> để đóng. Khoá và ghim KHÔNG đóng ticket — chúng vẫn nằm trong nhóm
    /// 5 issue mở của mỗi project.
    /// </summary>
    private sealed record Story(
        string Project, string Author, string Title, string Body,
        string[] Labels, string[] Assignees, string? Type, TicketPriority? Priority, int? Milestone,
        (string Author, string Body, bool Internal)[] Comments,
        string Outcome);

    /// <summary>
    /// 18 issue: 9 mỗi project, trong đó 5 mở và 4 đóng.
    ///
    /// Người được giao việc luôn là thành viên của chính project đó — giao cho người ngoài
    /// project thì tên hiện trên ticket mà người đó mở lên lại không thấy gì.
    /// </summary>
    private static Story[] Stories() =>
    new[]
    {
        // ------------------------------------------------------ Demo Alpha
        new Story(Alpha, "bao.long",
            "Đối soát thanh toán treo cho merchant khu vực EU",
            "Các lô đối soát cho merchant EU không rời khỏi trạng thái `PENDING` kể từ 09:40 UTC.\n\n"
            + "**Ảnh hưởng:** 1.284 lượt chi trả.\n\n"
            + "**Các bước tái hiện:** chạy đối soát với `region=eu-west-1` rồi soi outbox — message được ghi nhưng không ai tiêu thụ.",
            new[] { "incident", "billing", "eu-region" }, new[] { "thu.ha", "huy.kien" }, "Bug", TicketPriority.P0, 13,
            new[]
            {
                ("thu.ha", "Consumer của broker chết sau khi xoay khoá. Đang khởi động lại worker đối soát và phát lại outbox từ `sequence 918442`.", false),
                ("huy.kien", "Khoá mới chưa được đẩy sang vault path của EU. @thu.ha xác nhận giúp trước khi mình trả lời merchant.", true),
                ("ngoc.mai", "Bên mình vẫn chưa nhận được tiền về, có mốc thời gian dự kiến chưa ạ?", false),
                ("thu.ha", "Đã phát lại xong 812/1284 giao dịch. Phần còn lại chạy trong 30 phút tới.", false),
            },
            "pinned"),

        new Story(Alpha, "huy.kien",
            "Webhook trả 401 sau khi xoay khoá ký",
            "Sau khi xoay khoá ký, toàn bộ webhook trả về `401`. Chữ ký `X-Hub-Signature-256` vẫn đang tính bằng khoá cũ.",
            new[] { "integration", "bug" }, new[] { "quang.dung" }, "Bug", TicketPriority.P1, 13,
            new[]
            {
                ("quang.dung", "Đã tái hiện được ở staging. Secret trong bảng `webhook_subscriptions` chưa được cập nhật.", false),
                ("huy.kien", "Chờ phần phát lại outbox xong mới xoay lại khoá được, nếu không sẽ hỏng thêm một lượt nữa.", true),
                ("quang.dung", "Đã viết script cập nhật secret, chạy thử trên 3 subscription trước.", false),
            },
            "open"),

        new Story(Alpha, "ngoc.mai",
            "Yêu cầu hoàn tiền chưa hiện trên cổng khách hàng",
            "Mình yêu cầu hoàn tiền từ hôm kia, tiền đã trừ nhưng cổng khách hàng vẫn hiện `Đang xử lý`.",
            new[] { "feedback", "billing" }, new[] { "kim.chi" }, null, TicketPriority.P2, null,
            new[]
            {
                ("kim.chi", "Cảm ơn anh/chị đã báo. Bên em đang kiểm tra và phản hồi trong hôm nay.", false),
                ("kim.chi", "Khách hàng lần đầu gửi ticket, để ý giọng văn khi trả lời.", true),
                ("thanh.tung", "Đã đối chiếu với cổng thanh toán: giao dịch hoàn đã thành công, chỉ là projection của cổng khách hàng chưa cập nhật.", false),
            },
            "open"),

        new Story(Alpha, "hai.yen",
            "Cổng khách hàng đăng xuất sau mỗi 15 phút",
            "Cứ khoảng 15 phút là bị đăng xuất, phải đăng nhập lại từ đầu.",
            new[] { "bug", "regression" }, new[] { "minh.anh" }, "Bug", TicketPriority.P1, null,
            new[]
            {
                ("minh.anh", "Access token hạn 15 phút mà chưa có refresh token — đúng giới hạn đã biết của Release 1.", false),
                ("minh.anh", "Đây là hạn chế thiết kế, không phải lỗi. Cần quyết định sản phẩm trước khi mở lại thảo luận.", true),
                ("tuan.anh", "Đã ghi nhận vào backlog Release 2 kèm ước lượng.", false),
            },
            "locked"),

        new Story(Alpha, "minh.anh",
            "Rà soát vault path theo từng khu vực",
            "Rà lại đường dẫn vault theo từng khu vực để lần xoay khoá sau không bỏ sót EU.",
            new[] { "security", "needs-triage" }, new[] { "minh.anh", "tuan.anh" }, "Task", TicketPriority.P2, 14,
            new[]
            {
                ("tuan.anh", "Đã liệt kê được 7 khu vực, trong đó 2 khu vực dùng path đặt tay từ thời chưa có chuẩn.", false),
                ("minh.anh", "Hai path đặt tay đó là nguyên nhân gốc của sự cố EU. Ưu tiên chuẩn hoá trước.", true),
            },
            "open"),

        new Story(Alpha, "thu.ha",
            "Phát lại outbox đối soát",
            "Phát lại các message outbox chưa được tiêu thụ, bắt đầu từ `sequence 918442`.",
            new[] { "incident" }, new[] { "thu.ha" }, "Task", TicketPriority.P1, 13,
            new[]
            {
                ("thu.ha", "Đã phát lại xong toàn bộ 1.284 message. Đang đối chiếu số dư.", false),
                ("gia.bao", "Đã đối chiếu xong, số dư khớp. Bên em thông báo lại cho merchant.", false),
            },
            "closed-completed"),

        new Story(Alpha, "thu.ha",
            "p95 của /api/search/tickets tăng lên 2,4 giây",
            "p95 tăng từ 180ms lên 2,4s ngay sau khi bật full-text search trên bảng comment.",
            new[] { "performance", "regression" }, new[] { "thu.ha", "quang.dung" }, "Bug", TicketPriority.P1, 5,
            new[]
            {
                ("thu.ha", "Thiếu index GIN trên `ticket_search_comments.public_text`.", false),
                ("quang.dung", "Đã thêm index, p95 về 210ms. Có kèm migration nên môi trường nào cũng tự áp.", false),
            },
            "closed-completed"),

        new Story(Alpha, "bao.long",
            "Thêm chế độ tối cho cổng khách hàng",
            "Đề xuất bổ sung chế độ tối cho cổng khách hàng, theo thiết lập của hệ điều hành.",
            new[] { "enhancement", "wontfix" }, Array.Empty<string>(), "Feature", TicketPriority.P3, null,
            new[]
            {
                ("thanh.tung", "Khách hàng có hỏi vài lần, nhưng chưa ai coi là chặn việc.", false),
                ("bao.long", "Chưa nằm trong phạm vi quý này, đóng lại và ghi nhận vào backlog.", false),
            },
            "closed-not-planned"),

        new Story(Alpha, "van.khanh",
            "Hoàn tiền bị ghi nhận hai lần trong sao kê",
            "Đơn hoàn tiền của mình xuất hiện hai lần trong sao kê tháng này.",
            new[] { "billing", "duplicate" }, new[] { "gia.bao" }, null, TicketPriority.P2, null,
            new[]
            {
                ("gia.bao", "Nội dung trùng với một ticket đã có, bên em gộp lại để tiện theo dõi.", false),
                ("kim.chi", "Đã xác nhận là cùng một giao dịch, chỉ khác kênh gửi.", true),
            },
            "closed-duplicate"),

        // ------------------------------------------------------- Demo Beta
        new Story(Beta, "phuong.linh",
            "Mẫu thư tự động điền nhầm tên khách hàng",
            "Thư xác nhận tiếp nhận điền nhầm tên của khách hàng khác — rò rỉ dữ liệu giữa các khách hàng.",
            new[] { "bug", "security" }, new[] { "phuong.linh", "gia.bao" }, "Bug", TicketPriority.P0, 13,
            new[]
            {
                ("gia.bao", "Rò rỉ dữ liệu giữa các khách hàng, cần xử lý ngay.", false),
                ("gia.bao", "Đã tạm tắt thư tự động cho tới khi vá xong.", true),
                ("phuong.linh", "Nguyên nhân: biến template dùng chung một instance giữa các request.", false),
                ("khanh.chi", "Đã có bản vá, đang chờ review.", false),
            },
            "pinned"),

        new Story(Beta, "quang.dung",
            "Runbook chuyển vùng đã lỗi thời",
            "Runbook chuyển vùng vẫn trỏ tới cluster đã ngừng dùng từ quý trước.",
            new[] { "documentation" }, new[] { "quang.dung" }, "Task", TicketPriority.P3, 15,
            new[]
            {
                ("khanh.chi", "Mình cập nhật được phần mạng, phần database nhờ đội hạ tầng.", false),
                ("son.truong", "Ưu tiên thấp nhưng đừng để quá đợt phát hành sau — diễn tập quý tới dùng đúng tài liệu này.", true),
            },
            "open"),

        new Story(Beta, "thuy.trang",
            "Tìm kiếm không trả kết quả với tiếng Việt có dấu",
            "Gõ `thanh toán` thì không ra gì, gõ `thanh toan` thì ra đủ.",
            new[] { "bug", "needs-triage" }, new[] { "khanh.chi" }, "Bug", TicketPriority.P1, null,
            new[]
            {
                ("kim.chi", "Cảm ơn anh/chị, bên em tái hiện được và đã chuyển cho đội kỹ thuật.", false),
                ("khanh.chi", "Cấu hình text search đang dùng `simple` nên không chuẩn hoá dấu. Cần đổi sang `unaccent`.", false),
                ("son.truong", "Đổi cấu hình thì phải dựng lại toàn bộ index — sắp lịch vào cửa sổ bảo trì.", true),
            },
            "open"),

        new Story(Beta, "trung.hieu",
            "Giới hạn 20MB khi tải tệp đính kèm",
            "Mình cần gửi log dung lượng lớn nhưng hệ thống chặn ở 20MB.",
            new[] { "question", "enhancement" }, new[] { "phuong.linh" }, null, TicketPriority.P2, null,
            new[]
            {
                ("phuong.linh", "20MB là giới hạn hiện tại. Anh/chị nén lại hoặc chia nhỏ giúp em.", false),
                ("son.truong", "Nâng giới hạn phải tính cả chi phí lưu trữ, chưa quyết được ở đây. Khoá lại để khỏi loãng.", true),
            },
            "locked"),

        new Story(Beta, "son.truong",
            "Bổ sung xác thực hai lớp cho tài khoản nội bộ",
            "Tài khoản nội bộ hiện chỉ có mật khẩu. Đề xuất thêm TOTP cho các vai trò từ responder trở lên.",
            new[] { "security", "enhancement" }, new[] { "khanh.chi", "hai.dang" }, "Feature", TicketPriority.P2, 16,
            new[]
            {
                ("hai.dang", "Đã khảo sát: dùng TOTP là đủ, không cần SMS. Ước lượng 5 ngày công.", false),
                ("son.truong", "Ưu tiên cho vai trò manager và system trước, phần còn lại làm sau.", true),
                ("khanh.chi", "Đã dựng nhánh thử nghiệm, đăng nhập hai bước chạy được ở môi trường dev.", false),
            },
            "open"),

        new Story(Beta, "hai.dang",
            "Hàng đợi thông báo email ùn tắc từ 02:00",
            "Hàng đợi email tăng lên 40.000 message và không giảm. Thư xác nhận tới chậm 3 tiếng.",
            new[] { "incident", "performance" }, new[] { "hai.dang" }, "Bug", TicketPriority.P1, 13,
            new[]
            {
                ("hai.dang", "Một consumer bị treo ở kết nối SMTP không có timeout. Đã thêm timeout 30 giây.", false),
                ("quang.dung", "Hàng đợi đã rút hết sau 40 phút. Đã thêm cảnh báo khi hàng đợi vượt 5.000.", false),
            },
            "closed-completed"),

        new Story(Beta, "quang.dung",
            "Cập nhật tài liệu API công khai cho v1.2",
            "Bổ sung các endpoint mới và đánh dấu phần đã ngừng hỗ trợ trong tài liệu công khai.",
            new[] { "documentation" }, new[] { "quang.dung", "phuong.linh" }, "Task", TicketPriority.P3, 6,
            new[]
            {
                ("phuong.linh", "Đã rà lại phần ví dụ, có 4 chỗ còn dùng tham số cũ.", false),
                ("quang.dung", "Đã sửa đủ 4 chỗ và bổ sung mục ngừng hỗ trợ. Xong.", false),
            },
            "closed-completed"),

        new Story(Beta, "son.truong",
            "Hỗ trợ đăng nhập bằng tài khoản mạng xã hội",
            "Đề xuất cho phép đăng nhập bằng Google và Facebook để giảm ma sát khi đăng ký.",
            new[] { "enhancement", "wontfix" }, Array.Empty<string>(), "Feature", TicketPriority.P3, null,
            new[]
            {
                ("huy.kien", "Khách hàng doanh nghiệp không dùng tài khoản mạng xã hội, nên lợi ích không rõ.", false),
                ("son.truong", "Đóng lại: chi phí vận hành và rủi ro phụ thuộc bên thứ ba lớn hơn lợi ích ở thời điểm này.", false),
            },
            "closed-not-planned"),

        new Story(Beta, "kieu.oanh",
            "Không nhận được email xác nhận đăng ký",
            "Mình đăng ký từ sáng nhưng không thấy thư xác nhận, kiểm tra cả hộp thư rác rồi.",
            new[] { "bug", "duplicate" }, new[] { "huy.kien" }, null, TicketPriority.P2, null,
            new[]
            {
                ("huy.kien", "Đây là hệ quả của việc hàng đợi email bị ùn tắc, đã có ticket theo dõi riêng.", false),
                ("khanh.chi", "Gộp về ticket hàng đợi email cho gọn, không xử lý song song.", true),
            },
            "closed-duplicate"),
    };

    private static async Task SeedTicketsAsync(
        IServiceProvider root, Dictionary<string, Actor> actors, ILogger logger, CancellationToken ct)
    {
        var stories = Stories();
        await using (var probe = root.CreateAsyncScope())
        {
            var db = probe.ServiceProvider.GetRequiredService<AppDbContext>();
            var slugs = Projects.Select(p => p.Slug).ToHashSet();
            var want = stories.Length;
            var have = await db.Tickets.CountAsync(t => slugs.Contains(t.Project!.Slug), ct);

            // So theo SỐ LƯỢNG, không phải "đã có cái nào chưa". Một lần seed bị cắt giữa chừng
            // để lại vài ticket, và bản cũ thấy "có rồi" nên bỏ qua vĩnh viễn phần còn lại.
            if (have >= want)
            {
                logger.LogInformation("Issue trình diễn đã đủ ({Have}/{Want}), bỏ qua.", have, want);
                return;
            }

            if (have > 0)
            {
                logger.LogWarning(
                    "Issue trình diễn đang dở ({Have}/{Want}) — lần seed trước bị cắt giữa chừng. "
                    + "Xoá issue của hai project trình diễn rồi chạy lại để dựng trọn bộ.", have, want);
                return;
            }
        }

        logger.LogInformation("Seed {Count} issue trình diễn (có thể mất một phút)...", stories.Length);
        var created = new List<(string Project, int Number)>();

        for (var index = 0; index < stories.Length; index++)
        {
            var story = stories[index];
            var author = actors[story.Author];
            int number;

            await using (var scope = root.CreateAsyncScope())
            {
                var tickets = scope.ServiceProvider.GetRequiredService<TicketService>();
                // Nhân viên tạo hộ để có ngay label/assignee/milestone; khách hàng chỉ được
                // gửi tiêu đề và nội dung, đúng như mức quyền Read của GitHub.
                var opener = author.Role == "customer" ? author : actors[Lead];
                var response = await tickets.CreateAsync(story.Project, new CreateTicketRequest
                {
                    Title = story.Title,
                    Body = story.Body,
                    Labels = story.Labels.Concat(FillerLabels(index)).Distinct().ToList(),
                    Assignees = story.Assignees.Concat(FillerAssignees(story.Project, index))
                        .Distinct().Select(a => actors[a].User.Login).ToList(),
                    Type = story.Type,
                    Priority = story.Priority,
                    Milestone = story.Milestone,
                }, Principal(opener), null, null, ct);
                number = response.Number;
            }
            created.Add((story.Project, number));

            // Ticket do khách hàng gửi: mức quyền Read nên label/ưu tiên/milestone gửi kèm bị
            // bỏ qua (đúng như GitHub). Nhân viên phân loại ngay sau đó — vừa đúng quy trình
            // thật, vừa để timeline có event LABELED do người khác tác giả thực hiện.
            if (author.Role == "customer")
            {
                await using var scope = root.CreateAsyncScope();
                var tickets = scope.ServiceProvider.GetRequiredService<TicketService>();
                await tickets.UpdateAsync(story.Project, number, new UpdateTicketRequest
                {
                    Labels = story.Labels.Concat(FillerLabels(index)).Distinct().ToList(),
                    Assignees = story.Assignees.Concat(FillerAssignees(story.Project, index))
                        .Distinct().Select(a => actors[a].User.Login).ToList(),
                    Priority = story.Priority,
                    Milestone = story.Milestone,
                    Type = story.Type,
                }, Principal(actors["kim.chi"]), new DefaultHttpContext().Request, null, ct);
            }

            foreach (var (login, body, internalNote) in story.Comments)
            {
                await using var scope = root.CreateAsyncScope();
                var comments = scope.ServiceProvider.GetRequiredService<CommentService>();
                var principal = Principal(actors[login]);
                var request = new CommentRequest { Body = body };
                if (internalNote)
                {
                    await comments.InternalNoteAsync(story.Project, number, request, principal, ct);
                }
                else
                {
                    await comments.CreateAsync(story.Project, number, request, principal, ct);
                }
            }

            // Reaction trên chính ticket và trên bình luận đầu tiên — bản demo hiển thị cả hai.
            var reactors = ProjectStaff(story.Project);
            var types = new[] { ReactionType.ThumbsUp, ReactionType.Heart, ReactionType.Rocket, ReactionType.Eyes };
            foreach (var login in reactors.Take(4 + Rng.Next(0, 3)))
            {
                await using var scope = root.CreateAsyncScope();
                var reactions = scope.ServiceProvider.GetRequiredService<ReactionService>();
                await reactions.MutateAsync(story.Project, number, null, types[Rng.Next(types.Length)], "add",
                    Principal(actors[login]), ct);
            }

            if (story.Comments.Length > 0)
            {
                await using var scope = root.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var reactions = scope.ServiceProvider.GetRequiredService<ReactionService>();
                // TicketEvent cố ý không có navigation sang Ticket (bảng phân mảnh theo tháng),
                // nên phải tra id của ticket trước rồi mới lọc theo TicketId.
                var ticketId = await db.Tickets
                    .Where(t => t.Number == number && t.Project.Slug == story.Project)
                    .Select(t => t.Id)
                    .FirstAsync(ct);

                var firstComment = await db.TicketEvents
                    .Where(e => e.TicketId == ticketId && e.EventType == TicketEventTypes.Commented)
                    .OrderBy(e => e.Sequence)
                    .Select(e => (Guid?)e.Id)
                    .FirstOrDefaultAsync(ct);

                if (firstComment is { } eventId)
                {
                    foreach (var login in reactors.Take(3))
                    {
                        await reactions.MutateAsync(story.Project, number, eventId,
                            types[Rng.Next(types.Length)], "add", Principal(actors[login]), ct);
                    }
                }
            }

            await ApplyOutcomeAsync(root, actors, story, number, created, ct);
        }

        await LinkTicketsAsync(root, actors, created, ct);
        await CrossReferenceAsync(root, actors, created, ct);

        var open = stories.Count(s => s.Outcome is "open" or "locked" or "pinned");
        logger.LogInformation(
            "Đã seed {Count} issue ({Open} mở, {Closed} đóng) kèm bình luận, ghi chú nội bộ, reaction và quan hệ.",
            created.Count, open, stories.Length - open);
    }

    /// <summary>Nhân viên của một project — nguồn cho reaction và người được giao thêm.</summary>
    private static string[] ProjectStaff(string project) =>
        Staff.Where(p => p.Projects.Contains(project) && p.Role is "support" or "responder")
            .Select(p => p.Login).ToArray();

    /// <summary>
    /// Label phụ. Dùng chỉ số của kịch bản chứ không dùng <c>string.GetHashCode()</c>: .NET
    /// ngẫu nhiên hoá hash của chuỗi theo từng tiến trình, nên hash sẽ khiến mỗi lần dựng lại
    /// ra một bộ dữ liệu khác.
    /// </summary>
    private static IEnumerable<string> FillerLabels(int index)
    {
        string[] pool = { "needs-triage", "question", "help wanted", "documentation", "enhancement", "good first issue" };
        return Enumerable.Range(0, 3).Select(i => pool[(index * 3 + i) % pool.Length]);
    }

    /// <summary>
    /// Thêm người phụ trách để bảng phân công có chiều sâu — <b>chỉ lấy trong nội bộ project</b>.
    /// Lấy từ một danh sách chung sẽ giao việc cho người không thuộc project, và người đó mở
    /// hàng chờ của mình lên thì không thấy ticket vừa được giao.
    /// </summary>
    private static IEnumerable<string> FillerAssignees(string project, int index)
    {
        var pool = ProjectStaff(project);
        return Enumerable.Range(0, 3).Select(i => pool[(index * 2 + i) % pool.Length]);
    }

    /// <summary>Đưa ticket về đúng trạng thái cuối: đóng theo từng lý do, khoá, ghim.</summary>
    private static async Task ApplyOutcomeAsync(
        IServiceProvider root, Dictionary<string, Actor> actors, Story story, int number,
        List<(string Project, int Number)> created, CancellationToken ct)
    {
        if (story.Outcome == "open") return;

        await using var scope = root.CreateAsyncScope();
        var tickets = scope.ServiceProvider.GetRequiredService<TicketService>();
        var lead = Principal(actors[Lead]);
        var http = new DefaultHttpContext().Request; // Không gửi If-Match nên bỏ qua kiểm tra phiên bản.

        switch (story.Outcome)
        {
            case "closed-completed":
                await tickets.UpdateAsync(story.Project, number,
                    new UpdateTicketRequest { State = TicketState.Closed, StateReason = StateReason.Completed },
                    lead, http, null, ct);
                break;
            case "closed-not-planned":
                await tickets.UpdateAsync(story.Project, number,
                    new UpdateTicketRequest { State = TicketState.Closed, StateReason = StateReason.NotPlanned },
                    lead, http, null, ct);
                break;
            case "closed-duplicate":
                // Trỏ vào một ticket có thật của CHÍNH project này, chứ không phải một số hiệu
                // viết cứng: số hiệu chạy theo thứ tự tạo, nên viết cứng là hoặc trỏ nhầm, hoặc
                // trỏ vào chính nó khi thứ tự kịch bản đổi.
                var sibling = created.First(c => c.Project == story.Project && c.Number != number).Number;
                await tickets.UpdateAsync(story.Project, number,
                    new UpdateTicketRequest
                    {
                        State = TicketState.Closed,
                        StateReason = StateReason.Duplicate,
                        DuplicateOf = $"{sibling}",
                    },
                    lead, http, null, ct);
                break;
            case "locked":
                await tickets.LockAsync(story.Project, number, LockReason.Resolved, true, lead, ct);
                break;
            case "pinned":
                await tickets.PinAsync(story.Project, number, true, lead, ct);
                break;
        }
    }

    /// <summary>
    /// Nhắc tới ticket khác trong bình luận. Mỗi lần nhắc, ReferenceWorker sinh một event
    /// CROSS_REFERENCED và một bản ghi trong <c>ticket_references</c> — đúng như khi người thật
    /// gõ <c>#N</c> trên GitHub.
    /// </summary>
    private static async Task CrossReferenceAsync(
        IServiceProvider root, Dictionary<string, Actor> actors,
        List<(string Project, int Number)> created, CancellationToken ct)
    {
        var byProject = created.GroupBy(c => c.Project).ToDictionary(g => g.Key, g => g.Select(c => c.Number).ToList());

        foreach (var (project, numbers) in byProject)
        {
            if (numbers.Count < 2) continue;

            // Chỉ responder — không lấy cả support như những chỗ khác. Trong bộ này có ticket đã
            // khoá, mà BR-LIFECYCLE-02 chỉ cho người có `ticket.write` bình luận vào hội thoại
            // khoá; support không có quyền đó nên sẽ ném ngay giữa đợt seed.
            var voices = Staff
                .Where(p => p.Projects.Contains(project) && p.Role == "responder")
                .Select(p => p.Login).ToArray();

            for (var i = 0; i < numbers.Count; i++)
            {
                var other = numbers[(i + 1) % numbers.Count];
                var another = numbers[(i + 2) % numbers.Count];
                await using var scope = root.CreateAsyncScope();
                var comments = scope.ServiceProvider.GetRequiredService<CommentService>();
                await comments.CreateAsync(project, numbers[i], new CommentRequest
                {
                    Body = $"Liên quan tới #{other}; phần nguyên nhân đã bàn ở #{another}.",
                }, Principal(actors[voices[i % voices.Length]]), ct);
            }
        }
    }

    /// <summary>Sub-issue và quan hệ chặn — phần bản demo thể hiện ở cột Relationships.</summary>
    private static async Task LinkTicketsAsync(
        IServiceProvider root, Dictionary<string, Actor> actors,
        List<(string Project, int Number)> created, CancellationToken ct)
    {
        var lead = Principal(actors[Lead]);
        await using var scope = root.CreateAsyncScope();
        var relations = scope.ServiceProvider.GetRequiredService<RelationService>();

        foreach (var (project, _, _) in Projects)
        {
            var numbers = created.Where(c => c.Project == project).Select(c => c.Number).ToList();
            if (numbers.Count < 6) continue;

            // Issue đầu tiên là issue cha của ba việc tách ra.
            foreach (var child in new[] { numbers[4], numbers[5], numbers[6] })
            {
                await relations.AddSubIssueAsync(project, numbers[0], $"#{child}", lead, ct);
            }

            // Issue thứ hai bị chặn bởi issue thứ năm.
            await relations.AddBlockedByAsync(project, numbers[1], $"#{numbers[4]}", lead, ct);
        }
    }

    // ------------------------------------------------------------ board

    /// <summary>
    /// Hai board cho màn hình Kanban. Đi qua <see cref="BoardService"/> nên mỗi thẻ đều sinh
    /// event <c>ADDED_TO_BOARD</c> trên timeline của ticket, đúng như khi người dùng tự kéo vào.
    /// </summary>
    private static async Task SeedBoardsAsync(
        IServiceProvider root, Dictionary<string, Actor> actors, ILogger logger, CancellationToken ct)
    {
        await using var scope = root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Boards.AnyAsync(ct)) return;

        var boards = scope.ServiceProvider.GetRequiredService<BoardService>();
        var lead = Principal(actors[Lead]);

        // Ticket thật để xếp lên board: mở trước, đóng sau, theo đúng thứ tự số hiệu.
        var refs = await db.Tickets
            .OrderBy(t => t.Project!.Slug).ThenBy(t => t.Number)
            .Select(t => new { Slug = t.Project!.Slug, t.Number, t.State })
            .ToListAsync(ct);
        if (refs.Count == 0) return;

        var open = refs.Where(t => t.State == TicketState.Open).Select(t => $"{t.Slug}#{t.Number}").ToList();
        var closed = refs.Where(t => t.State == TicketState.Closed).Select(t => $"{t.Slug}#{t.Number}").ToList();

        var incident = await boards.CreateAsync(new BoardRequest
        {
            Name = "Incident response",
            Description = "Ticket sự cố xuyên project, tự nạp theo truy vấn.",
        }, lead, ct);

        // CreateAsync dựng sẵn Todo / In Progress / Done. Bản mẫu là Triage / In progress /
        // Blocked / Done, nên đổi tên cột đầu và chèn Blocked trước Done.
        var todoId = incident.Columns.Single(c => c.Name == "Todo").Id;
        var doneColumn = incident.Columns.Single(c => c.Name == "Done").Id;
        await boards.UpdateColumnAsync(incident.Id, todoId, new BoardColumnRequest { Name = "Triage", Position = 0 }, ct);
        await boards.UpdateColumnAsync(incident.Id, doneColumn, new BoardColumnRequest { Name = "Done", Position = 3 }, ct);
        incident = await boards.AddColumnAsync(incident.Id, new BoardColumnRequest { Name = "Blocked", Position = 2 }, ct);
        var columns = incident.Columns.OrderBy(c => c.Position).Select(c => c.Id).ToArray();

        await boards.UpdateAsync(incident.Id, new BoardRequest
        {
            Name = incident.Name,
            Description = incident.Description,
            Automation = JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new
            {
                auto_add_query = "is:open label:incident",
                item_closed_to_column_id = doneColumn,
            })),
        }, ct);

        // Ba ticket mở đầu tiên rải qua Triage / In Progress / Blocked, phần còn lại vào Triage.
        for (var i = 0; i < open.Count; i++)
        {
            var column = i < 3 ? columns[i] : columns[0];
            await AddBoardItemAsync(boards, incident.Id, new BoardItemRequest { Ticket = open[i], ColumnId = column }, lead, logger, ct);
        }
        foreach (var reference in closed)
        {
            await AddBoardItemAsync(boards, incident.Id, new BoardItemRequest { Ticket = reference, ColumnId = doneColumn }, lead, logger, ct);
        }
        await AddBoardItemAsync(boards, incident.Id,
            new BoardItemRequest { DraftTitle = "Rà soát vault path theo từng region", ColumnId = columns[0] }, lead, logger, ct);

        // Board thứ hai để danh sách board không chỉ có một dòng.
        var feedback = await boards.CreateAsync(new BoardRequest
        {
            Name = "Customer feedback",
            Description = "Phản hồi khách hàng chờ phân loại.",
        }, lead, ct);
        var firstColumn = feedback.Columns.OrderBy(c => c.Position).First().Id;
        foreach (var reference in open.Take(4))
        {
            await AddBoardItemAsync(boards, feedback.Id, new BoardItemRequest { Ticket = reference, ColumnId = firstColumn }, lead, logger, ct);
        }

        logger.LogInformation("Đã seed 2 board.");
    }

    /// <summary>Bỏ qua thẻ trùng thay vì làm hỏng cả đợt seed (BR-ORG-07 trả 409).</summary>
    private static async Task AddBoardItemAsync(BoardService boards, Guid boardId, BoardItemRequest request,
        ClaimsPrincipal actor, ILogger logger, CancellationToken ct)
    {
        try
        {
            await boards.AddItemAsync(boardId, request, actor, ct);
        }
        catch (AppException ex)
        {
            logger.LogWarning("Bỏ qua thẻ board {Ticket}: {Message}", request.Ticket ?? request.DraftTitle, ex.Message);
        }
    }

    // ------------------------------------------------- sự cố và phản hồi

    /// <summary>
    /// Bảng của Release 1: mỗi project 25 sự cố và 25 phản hồi, kèm lịch sử, bình luận, trả lời.
    ///
    /// Mọi bản ghi đều thuộc về một trong hai project trình diễn — không chừa phần
    /// <c>uncategorized</c> nữa, vì đặc tả dữ liệu là 25 mỗi loại cho mỗi project.
    /// </summary>
    private static async Task SeedLegacyAsync(
        IServiceProvider root, Dictionary<string, Actor> actors, ILogger logger, CancellationToken ct)
    {
        await using var scope = root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var target = IncidentsPerProject * Projects.Length;
        var existing = await db.Incidents.IgnoreQueryFilters()
            .CountAsync(i => i.Reporter.Email.EndsWith("@demo.local"), ct);
        if (existing >= target)
        {
            logger.LogInformation("Sự cố và phản hồi trình diễn đã đủ ({Have}/{Want}), bỏ qua.", existing, target);
            return;
        }

        var customers = Customers.Select(p => actors[p.Login].User).ToList();
        var severities = Enum.GetValues<IncidentSeverity>();
        var channels = Enum.GetValues<FeedbackChannel>();

        string[] titles =
        {
            "Cổng thanh toán trả lỗi 502", "Đăng nhập chậm bất thường", "Email xác nhận không gửi được",
            "Báo cáo cuối ngày thiếu dữ liệu", "Hàng đợi webhook ùn tắc", "Sao lưu đêm thất bại",
            "Chứng chỉ TLS sắp hết hạn", "Đối soát lệch số dư", "Tải trang danh sách quá 5 giây",
            "Không tạo được tài khoản mới", "Thông báo đẩy trùng lặp", "Bộ nhớ đệm không tự dọn",
        };

        string[] contents =
        {
            "Ứng dụng dùng ổn nhưng phần tìm kiếm hơi khó dùng.",
            "Nhân viên hỗ trợ trả lời rất nhanh, cảm ơn đội ngũ.",
            "Muốn có thêm bộ lọc theo khoảng thời gian.",
            "Thông báo qua email hơi nhiều, mong có tuỳ chọn tắt bớt.",
            "Giao diện mới nhìn dễ chịu hơn hẳn bản cũ.",
            "Xuất báo cáo ra Excel bị lỗi phông chữ tiếng Việt.",
        };

        var totalIncidents = 0;
        var totalFeedbacks = 0;

        foreach (var (slug, name, _) in Projects)
        {
            var projectId = await db.Projects.Where(p => p.Slug == slug).Select(p => p.Id).FirstAsync(ct);

            // Người xử lý phải là người của CHÍNH project này. Lấy từ danh sách chung sẽ gán sự
            // cố cho người không có quyền mở nó ra — bảng phân công đầy tên mà không ai làm được.
            var handlers = Staff
                .Where(p => p.Projects.Contains(slug) && p.Role is "support" or "responder")
                .Select(p => actors[p.Login].User)
                .ToList();

            var incidents = new List<Incident>();
            for (var i = 0; i < IncidentsPerProject; i++)
            {
                var reporter = customers[(i * 3 + slug.Length) % customers.Count];
                var assignee = handlers[i % handlers.Count];
                var createdAt = DateTimeOffset.UtcNow.AddDays(-100 + i * 3).AddHours(Rng.Next(0, 20));
                // 0–7 còn điều tra, 8–15 đang khắc phục, 16–24 đã xong: đủ ba trạng thái để lọc.
                var status = i < 8 ? IncidentStatus.Investigating
                    : i < 16 ? IncidentStatus.Mitigating
                    : IncidentStatus.Resolved;

                var incident = new Incident
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    Title = $"{titles[i % titles.Length]} — {name} #{i + 1}",
                    Description = "Ghi nhận từ giám sát tự động và đối chiếu với báo cáo của khách hàng.",
                    Severity = severities[i % severities.Length],
                    Status = status,
                    ReporterId = reporter.Id,
                    AssigneeId = assignee.Id,
                    CreatedAt = createdAt,
                    MitigatingAt = status >= IncidentStatus.Mitigating ? createdAt.AddHours(2) : null,
                    ResolvedAt = status == IncidentStatus.Resolved ? createdAt.AddHours(9) : null,
                    ResolvedBy = status == IncidentStatus.Resolved ? assignee.Id : null,
                };
                incidents.Add(incident);
                db.Incidents.Add(incident);

                if (status >= IncidentStatus.Mitigating)
                {
                    db.IncidentStatusHistory.Add(new IncidentStatusHistory
                    {
                        Id = Guid.NewGuid(), IncidentId = incident.Id,
                        FromStatus = IncidentStatus.Investigating, ToStatus = IncidentStatus.Mitigating,
                        ChangedBy = assignee.Id, ChangedAt = createdAt.AddHours(2),
                        Note = "Đã khoanh vùng nguyên nhân, đang áp dụng biện pháp giảm thiểu.",
                    });
                }
                if (status == IncidentStatus.Resolved)
                {
                    db.IncidentStatusHistory.Add(new IncidentStatusHistory
                    {
                        Id = Guid.NewGuid(), IncidentId = incident.Id,
                        FromStatus = IncidentStatus.Mitigating, ToStatus = IncidentStatus.Resolved,
                        ChangedBy = assignee.Id, ChangedAt = createdAt.AddHours(9),
                        Note = "Đã khắc phục và theo dõi ổn định trong 2 giờ.",
                    });
                }

                // Ít nhất hai bình luận cho MỌI sự cố: một của người báo, một của người xử lý.
                // Không dùng `i % 3` như bản trước — công thức đó để lại những sự cố chỉ có đúng
                // một dòng, mà đặc tả yêu cầu cái nào cũng phải có tương tác.
                db.IncidentComments.Add(new IncidentComment
                {
                    Id = Guid.NewGuid(), IncidentId = incident.Id, AuthorId = reporter.Id,
                    Body = "Bên mình vẫn gặp lỗi này, nhờ hỗ trợ sớm.",
                    CreatedAt = createdAt.AddHours(1),
                });
                db.IncidentComments.Add(new IncidentComment
                {
                    Id = Guid.NewGuid(), IncidentId = incident.Id, AuthorId = assignee.Id,
                    Body = "Đã tiếp nhận, đang kiểm tra log của dịch vụ liên quan.",
                    CreatedAt = createdAt.AddHours(2),
                });
                if (status == IncidentStatus.Resolved)
                {
                    db.IncidentComments.Add(new IncidentComment
                    {
                        Id = Guid.NewGuid(), IncidentId = incident.Id,
                        AuthorId = handlers[(i + 1) % handlers.Count].Id,
                        Body = "Đã khắc phục xong và theo dõi thêm 2 giờ, dịch vụ ổn định trở lại.",
                        CreatedAt = createdAt.AddHours(9),
                    });
                }
            }
            await db.SaveChangesAsync(ct);
            totalIncidents += incidents.Count;

            for (var i = 0; i < FeedbacksPerProject; i++)
            {
                var author = customers[(i * 7 + slug.Length) % customers.Count];
                var createdAt = DateTimeOffset.UtcNow.AddDays(-80 + i * 3).AddHours(Rng.Next(0, 18));
                // 0–4 mới, 5–12 đã gắn vào sự cố, 13–24 đã có người trả lời.
                var status = i < 5 ? FeedbackStatus.New
                    : i < 13 ? FeedbackStatus.Acknowledged
                    : FeedbackStatus.Responded;

                // Phản hồi gắn vào sự cố nào thì phải **cùng project** với sự cố đó — nếu không,
                // mở phản hồi ở project A lại thấy nó trỏ sang sự cố của project B.
                var linked = status >= FeedbackStatus.Acknowledged ? incidents[i % incidents.Count] : null;

                var feedback = new Feedback
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    Channel = channels[i % channels.Length],
                    CustomerEmail = author.Email,
                    Content = contents[i % contents.Length],
                    Status = status,
                    IncidentId = linked?.Id,
                    CreatedBy = author.Id,
                    CreatedAt = createdAt,
                };
                db.Feedbacks.Add(feedback);

                // Lời xác nhận tự động: luôn có, không đổi trạng thái (FR-BIZ-14).
                db.FeedbackReplies.Add(new FeedbackReply
                {
                    Id = Guid.NewGuid(), FeedbackId = feedback.Id, ResponderId = null, IsAutomatic = true,
                    Body = "Chúng tôi đã tiếp nhận phản hồi của bạn và sẽ phản hồi trong thời gian sớm nhất.",
                    CreatedAt = createdAt.AddSeconds(30),
                });

                // Câu trả lời của người thật chỉ đi kèm trạng thái Responded — đó chính là thứ
                // làm nên trạng thái đó (BR-BIZ-12). Gắn vào cả New/Acknowledged sẽ tạo ra dữ
                // liệu mà chính API của sản phẩm không bao giờ sinh ra được.
                if (status == FeedbackStatus.Responded)
                {
                    db.FeedbackReplies.Add(new FeedbackReply
                    {
                        Id = Guid.NewGuid(), FeedbackId = feedback.Id,
                        ResponderId = handlers[i % handlers.Count].Id, IsAutomatic = false,
                        Body = "Cảm ơn bạn đã góp ý. Chúng tôi đã ghi nhận và đưa vào kế hoạch cải tiến quý tới.",
                        CreatedAt = createdAt.AddHours(4),
                    });
                }
                totalFeedbacks++;
            }
            await db.SaveChangesAsync(ct);
        }

        var (strayIncidents, strayFeedbacks) = await SeedUnclassifiedAsync(db, actors, customers, ct);

        logger.LogInformation(
            "Đã seed {Incidents} sự cố (kèm lịch sử và bình luận) và {Feedbacks} phản hồi (kèm trả lời) "
            + "trên {Projects} project, cộng {StrayIncidents} sự cố và {StrayFeedbacks} phản hồi chưa phân loại.",
            totalIncidents, totalFeedbacks, Projects.Length, strayIncidents, strayFeedbacks);
    }

    /// <summary>
    /// Một nhúm nhỏ sự cố và phản hồi <b>không thuộc project nào</b> (<c>ProjectId = null</c>).
    ///
    /// Nằm ngoài con số 25 mỗi loại của mỗi project, và có lý do riêng: mục <c>uncategorized</c>
    /// là một project ảo chỉ tồn tại trong mã, và bước "mở uncategorized rồi chuyển sang dự án
    /// thật" của kịch bản demo cần đúng loại dữ liệu này. Gán hết cho project thì màn hình đó
    /// trống, và cái duy nhất nó chứng minh được lại không còn gì để xem.
    ///
    /// Giữ ở mức 3 mỗi loại: đủ để thao tác, không đủ để ai nhầm nó với dữ liệu chính.
    /// </summary>
    private static async Task<(int Incidents, int Feedbacks)> SeedUnclassifiedAsync(
        AppDbContext db, Dictionary<string, Actor> actors, List<User> customers, CancellationToken ct)
    {
        const int count = 3;
        // Người xử lý lấy trong nhóm phụ trách cả hai project: sự cố chưa phân loại chưa thuộc
        // về ai, nên giao cho người chỉ ở một project là giao cho người không có bối cảnh.
        var handlers = Staff
            .Where(p => p.Projects.Length == Projects.Length && p.Role is "support" or "responder")
            .Select(p => actors[p.Login].User)
            .ToList();

        string[] titles =
        {
            "Chưa rõ dịch vụ nào phát sinh lỗi 500 rải rác",
            "Khách báo chậm nhưng chưa xác định được luồng",
            "Cảnh báo giám sát không kèm tên dịch vụ",
        };

        var incidents = new List<Incident>();
        for (var i = 0; i < count; i++)
        {
            var reporter = customers[(i * 11) % customers.Count];
            var assignee = handlers[i % handlers.Count];
            var createdAt = DateTimeOffset.UtcNow.AddDays(-12 + i * 3);

            var incident = new Incident
            {
                Id = Guid.NewGuid(),
                ProjectId = null,
                Title = titles[i],
                Description = "Chưa gắn được vào project nào — chờ phân loại.",
                Severity = IncidentSeverity.Medium,
                Status = IncidentStatus.Investigating,
                ReporterId = reporter.Id,
                AssigneeId = assignee.Id,
                CreatedAt = createdAt,
            };
            incidents.Add(incident);
            db.Incidents.Add(incident);

            db.IncidentComments.Add(new IncidentComment
            {
                Id = Guid.NewGuid(), IncidentId = incident.Id, AuthorId = reporter.Id,
                Body = "Mình không rõ thuộc phần nào, nhờ bên mình phân loại giúp.",
                CreatedAt = createdAt.AddHours(1),
            });
            db.IncidentComments.Add(new IncidentComment
            {
                Id = Guid.NewGuid(), IncidentId = incident.Id, AuthorId = assignee.Id,
                Body = "Đã tiếp nhận. Đang tra log để xác định dịch vụ rồi mới gán project.",
                CreatedAt = createdAt.AddHours(3),
            });
        }

        for (var i = 0; i < count; i++)
        {
            var author = customers[(i * 13) % customers.Count];
            var createdAt = DateTimeOffset.UtcNow.AddDays(-10 + i * 3);
            var feedback = new Feedback
            {
                Id = Guid.NewGuid(),
                ProjectId = null,
                Channel = FeedbackChannel.Web,
                CustomerEmail = author.Email,
                Content = "Góp ý chung về hệ thống, chưa rõ thuộc dự án nào.",
                Status = FeedbackStatus.New,
                CreatedBy = author.Id,
                CreatedAt = createdAt,
            };
            db.Feedbacks.Add(feedback);
            db.FeedbackReplies.Add(new FeedbackReply
            {
                Id = Guid.NewGuid(), FeedbackId = feedback.Id, ResponderId = null, IsAutomatic = true,
                Body = "Chúng tôi đã tiếp nhận phản hồi của bạn và sẽ phản hồi trong thời gian sớm nhất.",
                CreatedAt = createdAt.AddSeconds(30),
            });
        }

        await db.SaveChangesAsync(ct);
        return (count, count);
    }

    // -------------------------------------------------------------- tiện

    private sealed record Actor(User User, string Role);

    /// <summary>
    /// Dựng danh tính đúng như middleware xác thực dựng sau mỗi request: claim <c>sub</c> để
    /// service biết ai đang thao tác, và toàn bộ permission của role để tầng phân quyền chạy thật.
    /// </summary>
    private static ClaimsPrincipal Principal(Actor actor)
    {
        var identity = new ClaimsIdentity("demo-seed");
        identity.AddClaim(new Claim(AppClaimTypes.Subject, actor.User.Id.ToString()));
        identity.AddClaim(new Claim(AppClaimTypes.Email, actor.User.Email));
        identity.AddClaim(new Claim(AppClaimTypes.Login, actor.User.Login));
        identity.AddClaim(new Claim(AppClaimTypes.DisplayName, actor.User.DisplayName));
        identity.AddClaim(new Claim(ClaimTypes.Role, actor.Role));

        foreach (var code in Permissions.DefaultRoles[actor.Role].Codes)
        {
            identity.AddClaim(new Claim(AppClaimTypes.Permission, code));
        }

        return new ClaimsPrincipal(identity);
    }
}
