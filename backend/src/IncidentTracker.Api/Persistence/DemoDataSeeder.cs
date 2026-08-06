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
/// Idempotent: đã có project <c>payments-core</c> thì bỏ qua toàn bộ.
/// </summary>
public static class DemoDataSeeder
{
    private const string Password = "Demo#12345";
    private static readonly Random Rng = new(20260906); // Cố định để mỗi lần dựng lại ra cùng dữ liệu.

    private sealed record Person(string Login, string Name, string Role);

    /// <summary>29 người + admin bootstrap = 30 tài khoản.</summary>
    private static readonly Person[] People =
    {
        new("bao.long", "Nguyễn Bảo Long", "admin"),
        new("son.truong", "Lê Sơn Trường", "responder"),
        new("huy.kien", "Nguyễn Huy Kiên", "support"),
        new("minh.anh", "Trần Minh Anh", "responder"),
        new("thu.ha", "Phạm Thu Hà", "responder"),
        new("quang.dung", "Đỗ Quang Dũng", "responder"),
        new("thanh.tung", "Vũ Thanh Tùng", "support"),
        new("kim.chi", "Hoàng Kim Chi", "support"),
        new("gia.bao", "Ngô Gia Bảo", "support"),
        new("phuong.linh", "Đặng Phương Linh", "support"),
        // Vai trò `viewer` đã bỏ (DatabaseInitializer.RetireRolesAsync). Ba tài khoản này chuyển
        // sang customer — nghĩa là từ chỗ tra cứu được mọi sự cố thành chỉ thấy phần của mình,
        // nhưng lại gửi được sự cố và phản hồi.
        new("tuan.kiet", "Lý Tuấn Kiệt", "customer"),
        new("my.duyen", "Trịnh Mỹ Duyên", "customer"),
        new("hoang.nam", "Bùi Hoàng Nam", "customer"),
        new("ngoc.mai", "Phan Ngọc Mai", "customer"),
        new("van.khanh", "Tạ Văn Khánh", "customer"),
        new("hai.yen", "Chu Hải Yến", "customer"),
        new("duc.thinh", "Mai Đức Thịnh", "customer"),
        new("bich.ngoc", "Lâm Bích Ngọc", "customer"),
        new("trung.hieu", "Dương Trung Hiếu", "customer"),
        new("lan.huong", "Đinh Lan Hương", "customer"),
        new("cong.vinh", "Hồ Công Vinh", "customer"),
        new("thuy.trang", "Nguyễn Thuỳ Trang", "customer"),
        new("anh.tu", "Võ Anh Tú", "customer"),
        new("kieu.oanh", "Trần Kiều Oanh", "customer"),
        new("dinh.phong", "Lê Đình Phong", "customer"),
        new("thu.hien", "Phạm Thu Hiền", "customer"),
        new("nhat.minh", "Đỗ Nhật Minh", "customer"),
        new("hong.nhung", "Vũ Hồng Nhung", "customer"),
        new("xuan.bach", "Hoàng Xuân Bách", "customer"),
    };

    private static readonly (string Slug, string Name, string Description)[] Projects =
    {
        ("payments-core", "Payments Core", "Thanh toán, đối soát và hoàn tiền"),
        ("web-platform", "Web Platform", "Cổng khách hàng và API công khai"),
        ("customer-support", "Customer Support", "Hàng đợi hỗ trợ khách hàng"),
    };

    /// <summary>18 label mỗi project — đủ để màn hình Labels và bộ lọc có chiều sâu.</summary>
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

    /// <summary>Người dùng, project, label, milestone, board. Ghi thẳng vì không có projection nào phụ thuộc.</summary>
    private static async Task<Dictionary<string, Actor>> SeedFoundationAsync(
        IServiceProvider root, ILogger logger, CancellationToken ct)
    {
        await using var scope = root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, ct);
        var actors = new Dictionary<string, Actor>();
        var createdAt = DateTimeOffset.UtcNow.AddDays(-120);

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
                await GrantDemoAccessAsync(db, project.Id, createdAt, ct);
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

        // Project mặc định do TicketSeeder dựng, không nằm trong danh sách trên, nhưng dữ liệu
        // demo vẫn trỏ vào nó — không cấp thì nó là project duy nhất cả dàn tài khoản không vào
        // được. Chỉ cấp khi chưa ai được cấp, để lần khởi động sau không dựng lại thứ người vận
        // hành vừa gỡ.
        var fallback = await db.Projects
            .Where(p => p.Slug == TicketSeeder.DefaultProjectSlug)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(ct);

        if (fallback != Guid.Empty
            && !await db.ProjectMembers.AnyAsync(m => m.ProjectId == fallback, ct)
            && !await db.ProjectRoleAccess.AnyAsync(a => a.ProjectId == fallback, ct))
        {
            await GrantDemoAccessAsync(db, fallback, createdAt, ct);
        }

        logger.LogInformation("Đã seed {Users} người dùng, {Projects} project, {Labels} label, {Milestones} milestone.",
            People.Length, Projects.Length, Projects.Length * 18, Projects.Length * 18);
        return actors;
    }

    /// <summary>
    /// Cấp quyền project cho dàn tài khoản demo.
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
    private static async Task GrantDemoAccessAsync(
        AppDbContext db, Guid projectId, DateTimeOffset now, CancellationToken ct)
    {
        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, r => r.Id, ct);

        // Nhân viên vào thẳng project với đúng vai trò của mình.
        var staff = await db.UserRoles.AsNoTracking()
            .Where(ur => db.Roles.Any(r => r.Id == ur.RoleId
                && (r.Name == "support" || r.Name == "responder" || r.Name == "manager")))
            .Select(ur => new { ur.UserId, ur.RoleId })
            .ToListAsync(ct);

        foreach (var row in staff)
        {
            db.ProjectMembers.Add(new ProjectMember
            {
                ProjectId = projectId, UserId = row.UserId, RoleId = row.RoleId, AddedAt = now
            });
        }

        // Khách hàng cấp theo vai trò, rồi tự nhận: quyền thật là giao của hai vế, nên thiếu vế
        // thứ hai thì dữ liệu demo vẫn trống với họ.
        if (roles.TryGetValue("customer", out var customerRole))
        {
            db.ProjectRoleAccess.Add(new ProjectRoleAccess
            {
                ProjectId = projectId, RoleId = customerRole, AddedAt = now
            });

            var customers = await db.UserRoles.AsNoTracking()
                .Where(ur => ur.RoleId == customerRole)
                .Select(ur => ur.UserId)
                .ToListAsync(ct);

            foreach (var userId in customers)
            {
                db.ProjectSubscriptions.Add(new ProjectSubscription
                {
                    ProjectId = projectId, UserId = userId, JoinedAt = now
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------- ticket

    /// <summary>Kịch bản của từng ticket, bám theo bản demo giao diện.</summary>
    private sealed record Story(
        string Project, string Author, string Title, string Body,
        string[] Labels, string[] Assignees, string? Type, TicketPriority? Priority, int? Milestone,
        (string Author, string Body, bool Internal)[] Comments,
        string Outcome);

    private static Story[] Stories() =>
    new[]
    {
        new Story("payments-core", "bao.long",
            "Payment settlement stuck for EU merchants",
            "Các lô đối soát cho merchant EU không rời khỏi trạng thái `PENDING` kể từ 09:40 UTC.\n\n"
            + "**Ảnh hưởng:** 1.284 lượt chi trả.\n\n"
            + "**Các bước tái hiện:** chạy đối soát với `region=eu-west-1` rồi soi outbox — message được ghi nhưng không ai tiêu thụ.",
            new[] { "incident", "billing", "eu-region" }, new[] { "son.truong", "huy.kien" }, "Bug", TicketPriority.P0, 13,
            new[]
            {
                ("son.truong", "Consumer của broker chết sau khi xoay khoá. Đang khởi động lại worker đối soát và phát lại outbox từ `sequence 918442`.", false),
                ("huy.kien", "Khoá mới chưa được đẩy sang vault path của EU. @son.truong xác nhận giúp trước khi mình trả lời merchant.", true),
                ("ngoc.mai", "Bên mình vẫn chưa nhận được tiền về, có mốc thời gian dự kiến chưa ạ?", false),
                ("son.truong", "Đã phát lại xong 812/1284 giao dịch. Phần còn lại chạy trong 30 phút tới.", false),
            },
            "open"),

        new Story("payments-core", "huy.kien",
            "Webhook deliveries failing with 401 after key rotation",
            "Sau khi xoay khoá ký, toàn bộ webhook trả về `401`. Chữ ký `X-Hub-Signature-256` tính bằng khoá cũ.",
            new[] { "integration", "bug" }, new[] { "huy.kien" }, "Bug", TicketPriority.P1, 13,
            new[]
            {
                ("minh.anh", "Đã tái hiện được ở staging. Secret trong bảng `webhook_subscriptions` chưa được cập nhật.", false),
                ("huy.kien", "Đang chờ #3 xong mới xoay lại được khoá.", false),
            },
            "open"),

        new Story("payments-core", "ngoc.mai",
            "Refund request not reflected in customer portal",
            "Mình yêu cầu hoàn tiền từ hôm kia, tiền đã trừ nhưng cổng khách hàng vẫn hiện `Đang xử lý`.",
            new[] { "feedback", "billing" }, Array.Empty<string>(), null, TicketPriority.P2, null,
            new[]
            {
                ("kim.chi", "Cảm ơn anh/chị đã báo. Bên em đang kiểm tra và phản hồi trong hôm nay.", false),
                ("kim.chi", "Khách hàng lần đầu gửi ticket, để ý giọng văn khi trả lời.", true),
            },
            "open"),

        new Story("payments-core", "son.truong",
            "Replay settlement outbox",
            "Phát lại các message outbox chưa được tiêu thụ, bắt đầu từ `sequence 918442`.",
            new[] { "incident" }, new[] { "son.truong" }, "Task", TicketPriority.P1, 13,
            new[] { ("son.truong", "Đã phát lại xong toàn bộ. Đang đối chiếu số dư.", false) },
            "closed-completed"),

        new Story("payments-core", "son.truong",
            "Notify affected merchants",
            "Gửi thông báo cho 1.284 merchant bị ảnh hưởng kèm mốc thời gian khắc phục.",
            new[] { "incident" }, new[] { "kim.chi" }, "Task", TicketPriority.P2, 13,
            new[] { ("kim.chi", "Đã soạn xong nội dung, chờ Lead duyệt.", false) },
            "open"),

        new Story("payments-core", "minh.anh",
            "Audit vault paths per region",
            "Rà lại đường dẫn vault theo từng khu vực để lần xoay khoá sau không bỏ sót EU.",
            new[] { "security", "needs-triage" }, new[] { "minh.anh" }, "Task", TicketPriority.P2, null,
            Array.Empty<(string, string, bool)>(),
            "open"),

        new Story("payments-core", "thu.ha",
            "Latency spike on /api/search/tickets",
            "p95 tăng từ 180ms lên 2,4s sau khi bật full-text search trên bảng comment.",
            new[] { "performance", "regression" }, new[] { "thu.ha" }, "Bug", TicketPriority.P1, 5,
            new[]
            {
                ("thu.ha", "Thiếu index GIN trên `ticket_search_comments.public_text`.", false),
                ("quang.dung", "Đã thêm index, p95 về 210ms.", false),
            },
            "closed-completed"),

        new Story("payments-core", "bao.long",
            "Add dark theme to customer portal",
            "Đề xuất thêm chế độ tối cho cổng khách hàng.",
            new[] { "enhancement", "wontfix" }, Array.Empty<string>(), "Feature", TicketPriority.P3, null,
            new[] { ("bao.long", "Chưa nằm trong phạm vi quý này, đóng lại và ghi nhận vào backlog.", false) },
            "closed-not-planned"),

        new Story("payments-core", "van.khanh",
            "Hoàn tiền bị tính trùng hai lần",
            "Đơn hoàn tiền của mình bị ghi nhận hai lần trong sao kê.",
            new[] { "billing", "duplicate" }, Array.Empty<string>(), null, TicketPriority.P2, null,
            new[] { ("kim.chi", "Nội dung trùng với một ticket đã có, bên em gộp lại để tiện theo dõi.", false) },
            "closed-duplicate"),

        new Story("web-platform", "quang.dung",
            "Region failover runbook out of date",
            "Runbook chuyển vùng vẫn trỏ tới cluster đã ngừng dùng từ quý trước.",
            new[] { "documentation" }, new[] { "quang.dung" }, "Task", TicketPriority.P3, null,
            new[] { ("thanh.tung", "Mình cập nhật được phần mạng, phần database nhờ team hạ tầng.", false) },
            "open"),

        new Story("web-platform", "hai.yen",
            "Cổng khách hàng đăng xuất liên tục",
            "Cứ khoảng 15 phút là bị đăng xuất, phải đăng nhập lại.",
            new[] { "bug", "regression" }, new[] { "minh.anh" }, "Bug", TicketPriority.P1, null,
            new[]
            {
                ("minh.anh", "Access token hạn 15 phút mà chưa có refresh token — đúng giới hạn đã biết của Release 1.", false),
                ("minh.anh", "Đây là hạn chế thiết kế, không phải lỗi. Cần quyết định sản phẩm.", true),
            },
            "locked"),

        new Story("customer-support", "phuong.linh",
            "Mẫu trả lời tự động gửi sai tên khách hàng",
            "Thư xác nhận tiếp nhận điền nhầm tên của khách hàng khác.",
            new[] { "bug", "security" }, new[] { "phuong.linh", "gia.bao" }, "Bug", TicketPriority.P0, null,
            new[]
            {
                ("gia.bao", "Rò rỉ dữ liệu giữa các khách hàng, cần xử lý ngay.", false),
                ("gia.bao", "Đã tạm tắt thư tự động cho tới khi vá xong.", true),
                ("phuong.linh", "Nguyên nhân: biến template dùng chung một instance giữa các request.", false),
            },
            "pinned"),
    };

    private static async Task SeedTicketsAsync(
        IServiceProvider root, Dictionary<string, Actor> actors, ILogger logger, CancellationToken ct)
    {
        await using (var probe = root.CreateAsyncScope())
        {
            var db = probe.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Tickets.AnyAsync(t => t.Project!.Slug == Projects[0].Slug, ct))
            {
                logger.LogInformation("Ticket trình diễn đã có, bỏ qua.");
                return;
            }
        }

        logger.LogInformation("Seed ticket trình diễn (có thể mất một phút)...");
        var created = new List<(string Project, int Number)>();

        var stories = Stories();
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
                var opener = author.Role == "customer" ? author : actors["bao.long"];
                var response = await tickets.CreateAsync(story.Project, new CreateTicketRequest
                {
                    Title = story.Title,
                    Body = story.Body,
                    Labels = story.Labels.Concat(FillerLabels(index)).Distinct().ToList(),
                    Assignees = story.Assignees.Concat(FillerAssignees(index))
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
            var reactors = new[] { "minh.anh", "thu.ha", "kim.chi", "thanh.tung", "gia.bao", "quang.dung" };
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

            await ApplyOutcomeAsync(root, actors, story, number, ct);
        }

        await LinkTicketsAsync(root, actors, created, ct);
        await CrossReferenceAsync(root, actors, created, ct);
        logger.LogInformation("Đã seed {Count} ticket kèm bình luận, ghi chú nội bộ, reaction và quan hệ.", created.Count);
    }

    /// <summary>
    /// Label phụ. Dùng chỉ số của kịch bản chứ không dùng <c>string.GetHashCode()</c>: .NET
    /// ngẫu nhiên hoá hash của chuỗi theo từng tiến trình, nên hash sẽ khiến mỗi lần dựng lại
    /// ra một bộ dữ liệu khác.
    /// </summary>
    private static IEnumerable<string> FillerLabels(int index)
    {
        string[] pool = { "needs-triage", "question", "help wanted", "documentation", "enhancement", "good first issue" };
        return Enumerable.Range(0, 5).Select(i => pool[(index * 3 + i) % pool.Length]);
    }

    /// <summary>Thêm người phụ trách để bảng phân công và danh sách theo dõi có chiều sâu.</summary>
    private static IEnumerable<string> FillerAssignees(int index)
    {
        string[] pool = { "minh.anh", "thu.ha", "quang.dung", "thanh.tung", "kim.chi", "gia.bao", "phuong.linh" };
        return Enumerable.Range(0, 5).Select(i => pool[(index * 2 + i) % pool.Length]);
    }

    /// <summary>Đưa ticket về đúng trạng thái cuối: đóng theo từng lý do, khoá, ghim.</summary>
    private static async Task ApplyOutcomeAsync(
        IServiceProvider root, Dictionary<string, Actor> actors, Story story, int number, CancellationToken ct)
    {
        if (story.Outcome == "open") return;

        await using var scope = root.CreateAsyncScope();
        var tickets = scope.ServiceProvider.GetRequiredService<TicketService>();
        var lead = Principal(actors["bao.long"]);
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
                await tickets.UpdateAsync(story.Project, number,
                    new UpdateTicketRequest { State = TicketState.Closed, StateReason = StateReason.Duplicate, DuplicateOf = "3" },
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
        string[] voices = { "minh.anh", "thu.ha", "quang.dung", "kim.chi", "thanh.tung" };
        var byProject = created.GroupBy(c => c.Project).ToDictionary(g => g.Key, g => g.Select(c => c.Number).ToList());

        foreach (var (project, numbers) in byProject)
        {
            if (numbers.Count < 2) continue;

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
        var core = created.Where(c => c.Project == "payments-core").Select(c => c.Number).ToList();
        if (core.Count < 6) return;

        var lead = Principal(actors["bao.long"]);
        await using var scope = root.CreateAsyncScope();
        var relations = scope.ServiceProvider.GetRequiredService<RelationService>();

        // Ticket #1 là ticket cha của ba việc tách ra (#4, #5, #6).
        foreach (var child in new[] { core[3], core[4], core[5] })
        {
            await relations.AddSubIssueAsync("payments-core", core[0], $"#{child}", lead, ct);
        }

        // Ticket #2 bị chặn bởi #6 (chưa rà xong vault path thì chưa xoay lại khoá được).
        await relations.AddBlockedByAsync("payments-core", core[1], $"#{core[5]}", lead, ct);
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
        var lead = Principal(actors["bao.long"]);

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

        // Ba ticket mở đầu tiên rải qua Todo / In Progress / Blocked, phần còn lại vào Todo.
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
        foreach (var reference in open.Take(3))
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

    /// <summary>Bảng của Release 1: 60 sự cố và 60 phản hồi kèm lịch sử, bình luận, trả lời.</summary>
    private static async Task SeedLegacyAsync(
        IServiceProvider root, Dictionary<string, Actor> actors, ILogger logger, CancellationToken ct)
    {
        await using var scope = root.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (await db.Incidents.IgnoreQueryFilters().CountAsync(i => i.Reporter.Email.EndsWith("@demo.local"), ct) >= 60)
        {
            logger.LogInformation("Sự cố và phản hồi trình diễn đã có, bỏ qua.");
            return;
        }

        var staff = actors.Values.Where(a => a.Role is "responder" or "support").Select(a => a.User).ToList();
        var customers = actors.Values.Where(a => a.Role == "customer").Select(a => a.User).ToList();
        var severities = Enum.GetValues<IncidentSeverity>();

        // Sự cố và phản hồi giờ thuộc về project. Rải đều qua các project để dữ liệu trình diễn
        // cho thấy đúng cái mà việc chia project sinh ra, và cố ý chừa một phần **không gắn
        // project nào** để mục `uncategorized` có dữ liệu thật mà nhìn.
        var projectIds = await db.Projects.OrderBy(p => p.Slug).Select(p => p.Id).ToListAsync(ct);
        Guid? ProjectFor(int i) => i % 7 == 6 || projectIds.Count == 0 ? null : projectIds[i % projectIds.Count];

        string[] titles =
        {
            "Cổng thanh toán trả lỗi 502", "Đăng nhập chậm bất thường", "Email xác nhận không gửi được",
            "Báo cáo cuối ngày thiếu dữ liệu", "Hàng đợi webhook ùn tắc", "Sao lưu đêm thất bại",
            "Chứng chỉ TLS sắp hết hạn", "Đối soát lệch số dư", "Tải trang danh sách quá 5 giây",
            "Không tạo được tài khoản mới", "Thông báo đẩy trùng lặp", "Bộ nhớ đệm không tự dọn",
        };

        var incidents = new List<Incident>();
        for (var i = 0; i < 60; i++)
        {
            var reporter = customers[i % customers.Count];
            var assignee = staff[i % staff.Count];
            var createdAt = DateTimeOffset.UtcNow.AddDays(-100 + i).AddHours(Rng.Next(0, 20));
            // 0–19 còn điều tra, 20–39 đang khắc phục, 40–59 đã xong: đủ ba trạng thái để lọc.
            var status = i < 20 ? IncidentStatus.Investigating : i < 40 ? IncidentStatus.Mitigating : IncidentStatus.Resolved;

            var incident = new Incident
            {
                Id = Guid.NewGuid(),
                ProjectId = ProjectFor(i),
                Title = $"{titles[i % titles.Length]} (#{i + 1})",
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

            for (var c = 0; c <= i % 3; c++)
            {
                db.IncidentComments.Add(new IncidentComment
                {
                    Id = Guid.NewGuid(), IncidentId = incident.Id,
                    AuthorId = c == 0 ? reporter.Id : staff[(i + c) % staff.Count].Id,
                    Body = c == 0 ? "Bên mình vẫn gặp lỗi này, nhờ hỗ trợ sớm." : "Đã tiếp nhận, đang kiểm tra log của dịch vụ liên quan.",
                    CreatedAt = createdAt.AddHours(1 + c),
                });
            }
        }
        await db.SaveChangesAsync(ct);

        string[] contents =
        {
            "Ứng dụng dùng ổn nhưng phần tìm kiếm hơi khó dùng.",
            "Nhân viên hỗ trợ trả lời rất nhanh, cảm ơn đội ngũ.",
            "Muốn có thêm bộ lọc theo khoảng thời gian.",
            "Thông báo qua email hơi nhiều, mong có tuỳ chọn tắt bớt.",
            "Giao diện mới nhìn dễ chịu hơn hẳn bản cũ.",
            "Xuất báo cáo ra Excel bị lỗi phông chữ tiếng Việt.",
        };
        var channels = Enum.GetValues<FeedbackChannel>();

        for (var i = 0; i < 60; i++)
        {
            var author = customers[i % customers.Count];
            var createdAt = DateTimeOffset.UtcNow.AddDays(-80 + i).AddHours(Rng.Next(0, 18));
            // 0–19 mới, 20–39 đã gắn vào sự cố, 40–59 đã có người trả lời.
            var status = i < 20 ? FeedbackStatus.New : i < 40 ? FeedbackStatus.Acknowledged : FeedbackStatus.Responded;

            // Phản hồi gắn vào sự cố nào thì phải **cùng project** với sự cố đó — nếu không, mở
            // phản hồi ở project A lại thấy nó trỏ sang sự cố của project B.
            var linked = status >= FeedbackStatus.Acknowledged ? incidents[i % incidents.Count] : null;

            var feedback = new Feedback
            {
                Id = Guid.NewGuid(),
                ProjectId = linked?.ProjectId ?? ProjectFor(i),
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

            if (status == FeedbackStatus.Responded)
            {
                db.FeedbackReplies.Add(new FeedbackReply
                {
                    Id = Guid.NewGuid(), FeedbackId = feedback.Id,
                    ResponderId = staff[i % staff.Count].Id, IsAutomatic = false,
                    Body = "Cảm ơn bạn đã góp ý. Chúng tôi đã ghi nhận và đưa vào kế hoạch cải tiến quý tới.",
                    CreatedAt = createdAt.AddHours(4),
                });
            }
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Đã seed 60 sự cố (kèm lịch sử và bình luận) và 60 phản hồi (kèm trả lời).");
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
