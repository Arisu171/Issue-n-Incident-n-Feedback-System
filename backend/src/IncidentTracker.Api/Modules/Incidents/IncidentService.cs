using System.Data;
using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Observability;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Incidents;

/// <summary>
/// Điểm chèn lỗi cho fault test TC-NFR-02. Mặc định là no-op; test đăng ký bản cài đặt
/// ném lỗi ngay giữa transaction để kiểm chứng NFR-REL-01 (rollback trọn vẹn).
/// </summary>
public interface ITransactionFaultHook
{
    Task AfterHistoryAppendedAsync(Guid incidentId, CancellationToken ct);
}

public sealed class NoOpTransactionFaultHook : ITransactionFaultHook
{
    public Task AfterHistoryAppendedAsync(Guid incidentId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// CMP-08 Incident Service + State Machine.
/// Giữ toàn bộ invariant của aggregate ENT-Incident (mục 5.3) và là nơi duy nhất
/// mở transaction cho cặp "đổi trạng thái + ghi lịch sử" (DRV-06, FR-BIZ-05).
/// </summary>
public sealed class IncidentService
{
    private readonly AppDbContext _db;
    private readonly ITransactionFaultHook _faultHook;
    private readonly IOptionsMonitor<SlaOptions> _sla;
    private readonly AppMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly IAuthorizationService _authorization;
    private readonly ILogger<IncidentService> _logger;

    public IncidentService(AppDbContext db, ITransactionFaultHook faultHook,
        IOptionsMonitor<SlaOptions> sla, AppMetrics metrics, TimeProvider clock,
        IAuthorizationService authorization, ILogger<IncidentService> logger)
    {
        _db = db;
        _faultHook = faultHook;
        _sla = sla;
        _metrics = metrics;
        _clock = clock;
        _authorization = authorization;
        _logger = logger;
    }

    // ---------------- UC-BIZ-01 · Ghi nhận sự cố ----------------

    /// <summary>
    /// FR-BIZ-01 · SEQ-04. Trạng thái khởi tạo luôn là <c>Investigating</c> và
    /// <c>reporter_id</c> lấy từ claim <c>sub</c> — hai giá trị này không nhận từ body.
    /// </summary>
    public async Task<IncidentResponse> CreateAsync(
        CreateIncidentRequest request, Guid reporterId, Guid? projectId, CancellationToken ct)
    {
        var incident = new Incident
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = request.Title.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            Severity = request.Severity,
            Status = IncidentStateMachine.InitialStatus,
            ReporterId = reporterId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Incidents.Add(incident);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit incident.create actor={ActorId} incident={IncidentId} severity={Severity} result=success",
            reporterId, incident.Id, incident.Severity);

        return await LoadResponseAsync(incident.Id, ct);
    }

    // ---------------- UC-BIZ-02/03 · Chuyển trạng thái ----------------

    /// <summary>
    /// FR-BIZ-02/03/05 · SEQ-05. Khóa bản ghi bằng <c>SELECT ... FOR UPDATE</c> để hai
    /// Responder bấm cùng lúc không cùng đi qua kiểm tra transition (mục 6.5); việc đổi
    /// status và ghi lịch sử nằm trong đúng một transaction.
    /// </summary>
    public async Task<IncidentResponse> TransitionStatusAsync(
        Guid incidentId, UpdateStatusRequest request, ClaimsPrincipal caller, Guid? projectId, CancellationToken ct)
    {
        var actorId = caller.GetUserId();

        // EnableRetryOnFailure không cho phép transaction do người dùng tự mở, nên cả khối
        // phải chạy như một đơn vị retriable. Chỉ lỗi transient của Npgsql mới được thử lại;
        // AppException (404/409) không nằm trong tập đó nên không bao giờ bị lặp.
        var strategy = _db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // Lần thử lại phải bắt đầu từ trạng thái sạch, nếu không entity cũ vẫn bị theo dõi.
            _db.ChangeTracker.Clear();

            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

            var incident = await LockAsync(incidentId, ct)
                ?? throw AppException.NotFound($"Không tìm thấy sự cố '{incidentId}'.");
            EnsureInScope(incident, projectId);

            // BR-BIZ-10 — chống BOLA. Kiểm tra nằm SAU khi khóa hàng: trước đó chưa biết ai
            // đang phụ trách, và nếu đọc assignee ngoài transaction thì hai request song song
            // có thể cùng đi qua cửa này.
            var ownership = await _authorization.AuthorizeAsync(
                caller, incident, ResourceOperationRequirement.Write);

            if (!ownership.Succeeded)
            {
                throw new AppException(
                    StatusCodes.Status403Forbidden,
                    "Không đủ quyền",
                    "Sự cố này đang do người khác phụ trách. Chỉ người được giao xử lý mới "
                    + $"chuyển được trạng thái, hoặc cần permission '{Permissions.IncidentManageAny}'.",
                    new Dictionary<string, object?>
                    {
                        ["requiredPermission"] = Permissions.IncidentManageAny,
                        ["assigneeId"] = incident.AssigneeId
                    });
            }

            var from = incident.Status;
            var to = request.TargetStatus;

            if (!IncidentStateMachine.CanTransition(from, to))
            {
                // NFR-USE-01 — 409 luôn kèm currentStatus và allowedNextStatus.
                throw AppException.Conflict(
                    BuildTransitionMessage(from, to),
                    new Dictionary<string, object?>
                    {
                        ["currentStatus"] = from.ToString(),
                        ["requestedStatus"] = to.ToString(),
                        ["allowedNextStatus"] = IncidentStateMachine.AllowedNext(from)?.ToString()
                    });
            }

            var now = DateTimeOffset.UtcNow;
            incident.Status = to;

            // Sự cố chưa ai nhận thì người thao tác đầu tiên trở thành người phụ trách. Nếu
            // không có bước này, quy tắc "chỉ assignee được chuyển trạng thái" sẽ khóa cứng
            // mọi sự cố mới cho tới khi admin gán tay — mà UC-BIZ-06 chỉ là Should.
            var claimed = ResourceAccessRules.ShouldClaim(caller, incident.AssigneeId);
            if (claimed)
            {
                incident.AssigneeId = actorId;
            }

            if (to == IncidentStatus.Mitigating)
            {
                incident.MitigatingAt = now;
            }
            else if (to == IncidentStatus.Resolved)
            {
                incident.ResolvedAt = now;
                incident.ResolvedBy = actorId;
            }

            // BR-BIZ-06 — đúng một dòng lịch sử cho mỗi lần chuyển thành công.
            _db.IncidentStatusHistory.Add(new IncidentStatusHistory
            {
                Id = Guid.NewGuid(),
                IncidentId = incident.Id,
                FromStatus = from,
                ToStatus = to,
                ChangedBy = actorId,
                ChangedAt = now,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
            });

            await _db.SaveChangesAsync(ct);
            await _faultHook.AfterHistoryAppendedAsync(incident.Id, ct);
            await tx.CommitAsync(ct);

            _metrics.StatusTransitioned(from.ToString(), to.ToString());
            _logger.LogInformation(
                "Audit incident.status actor={ActorId} incident={IncidentId} from={From} to={To} claimed={Claimed} result=success",
                actorId, incident.Id, from, to, claimed);
        });

        return await LoadResponseAsync(incidentId, ct);
    }

    private static string BuildTransitionMessage(IncidentStatus from, IncidentStatus to)
    {
        var next = IncidentStateMachine.AllowedNext(from);

        if (from == to)
        {
            return $"Sự cố đã ở trạng thái '{from}'; hệ thống không chấp nhận chuyển trùng trạng thái.";
        }

        return next is null
            ? $"Sự cố đã ở trạng thái cuối '{from}'; không còn bước chuyển nào hợp lệ."
            : $"Không thể chuyển từ '{from}' sang '{to}'. Bước hợp lệ tiếp theo là '{next}'.";
    }

    // ---------------- UC-BIZ-06 · Gán người xử lý ----------------

    /// <summary>
    /// FR-BIZ-06 · API-Incident-Assign — idempotent. BR-BIZ-08 đòi assignee phải active và
    /// có <c>incident.update_status</c>; vi phạm trả 409 theo TC-BIZ-07.
    /// </summary>
    public async Task AssignAsync(Guid incidentId, Guid assigneeId, Guid actorId, Guid? projectId, CancellationToken ct)
    {
        var incident = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy sự cố '{incidentId}'.");
        EnsureInScope(incident, projectId);

        if (incident.Status == IncidentStatus.Resolved)
        {
            throw AppException.Conflict("Sự cố đã đóng nên không thể đổi người xử lý.");
        }

        var assignee = await _db.Users.AsNoTracking()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                .ThenInclude(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .FirstOrDefaultAsync(u => u.Id == assigneeId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy user '{assigneeId}'.");

        if (!assignee.IsActive)
        {
            throw AppException.Conflict($"User '{assignee.Email}' đang bị vô hiệu hóa nên không thể nhận sự cố.");
        }

        var hasPermission = assignee.UserRoles
            .SelectMany(ur => ur.Role.RolePermissions)
            .Any(rp => rp.Permission.Code == Permissions.IncidentUpdateStatus);

        if (!hasPermission)
        {
            throw AppException.Conflict(
                $"User '{assignee.Email}' không có permission '{Permissions.IncidentUpdateStatus}' nên không thể nhận sự cố.");
        }

        if (incident.AssigneeId == assigneeId)
        {
            return; // Gọi lại với cùng assignee vẫn 204.
        }

        incident.AssigneeId = assigneeId;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit incident.assign actor={ActorId} incident={IncidentId} assignee={AssigneeId} result=success",
            actorId, incidentId, assigneeId);
    }

    // ---------------- UC-BIZ-07 · Xóa mềm ----------------

    /// <summary>
    /// FR-BIZ-04 · ADR-003. Chỉ sự cố đã <c>Resolved</c> mới xóa được; gọi lần hai vẫn trả 204
    /// nên phải bỏ qua global query filter, nếu không lần hai sẽ ra 404 thay vì idempotent.
    /// </summary>
    public async Task SoftDeleteAsync(Guid incidentId, Guid actorId, Guid? projectId, CancellationToken ct)
    {
        var incident = await _db.Incidents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy sự cố '{incidentId}'.");
        EnsureInScope(incident, projectId);

        if (incident.IsDeleted)
        {
            return;
        }

        if (incident.Status != IncidentStatus.Resolved)
        {
            throw AppException.Conflict(
                $"Chỉ được xóa mềm sự cố đã ở trạng thái 'Resolved'; sự cố này đang ở '{incident.Status}'.",
                new Dictionary<string, object?>
                {
                    ["currentStatus"] = incident.Status.ToString(),
                    ["allowedNextStatus"] = IncidentStateMachine.AllowedNext(incident.Status)?.ToString()
                });
        }

        incident.IsDeleted = true;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit incident.delete actor={ActorId} incident={IncidentId} result=success",
            actorId, incidentId);
    }

    // ---------------- UC-BIZ-05 · Tra cứu ----------------

    /// <summary>
    /// FR-BIZ-08. Query filter của EF tự loại bản ghi đã xóa mềm (ADR-003).
    ///
    /// BR-BIZ-09 — người không có <c>incident.read.all</c> chỉ thấy sự cố của mình. Lọc ngay
    /// trong SQL chứ không lọc sau khi nạp: nếu lọc trong bộ nhớ thì tổng số bản ghi và số
    /// trang vẫn tính trên dữ liệu của người khác, tức là vẫn rò rỉ thông tin.
    /// </summary>
    public async Task<PagedResult<IncidentResponse>> ListAsync(
        Guid? projectId, IncidentListQuery query, ClaimsPrincipal caller, CancellationToken ct)
    {
        // Lọc theo project **trong câu truy vấn**, trước cả bộ lọc quyền sở hữu: lọc sau khi lấy
        // về thì phân trang tính trên dữ liệu của project khác, tức con số tổng đã rò rỉ.
        var q = BaseQuery().Where(i => i.ProjectId == projectId);

        if (ResourceAccessRules.IncidentVisibilityFilter(caller) is { } visibility)
        {
            q = q.Where(visibility);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            // ILIKE qua EF.Functions: so khớp không phân biệt hoa thường ngay trong Postgres.
            var pattern = "%" + query.Q.Trim() + "%";
            q = q.Where(i => EF.Functions.ILike(i.Title, pattern)
                || (i.Description != null && EF.Functions.ILike(i.Description, pattern)));
        }

        if (query.Status is not null)
        {
            q = q.Where(i => i.Status == query.Status.Value);
        }

        if (query.AssigneeId is not null)
        {
            q = q.Where(i => i.AssigneeId == query.AssigneeId.Value);
        }

        if (query.Severity is not null)
        {
            q = q.Where(i => i.Severity == query.Severity.Value);
        }

        if (query.CreatedFrom is not null)
        {
            q = q.Where(i => i.CreatedAt >= query.CreatedFrom.Value);
        }

        if (query.CreatedTo is not null)
        {
            q = q.Where(i => i.CreatedAt <= query.CreatedTo.Value);
        }

        if (query.SlaBreachedOnly == true)
        {
            // Dịch thành điều kiện SQL để tận dụng index thay vì lọc sau khi đã nạp lên bộ nhớ.
            var options = _sla.CurrentValue;
            var now = _clock.GetUtcNow();
            var investigatingCutoff = now.AddHours(-options.InvestigatingHours);
            var mitigatingCutoff = now.AddHours(-options.MitigatingHours);

            q = q.Where(i =>
                (i.Status == IncidentStatus.Investigating && i.CreatedAt < investigatingCutoff)
                || (i.Status == IncidentStatus.Mitigating
                    && (i.MitigatingAt ?? i.CreatedAt) < mitigatingCutoff));
        }

        var total = await q.CountAsync(ct);

        q = query.OldestFirst
            ? q.OrderBy(i => i.CreatedAt).ThenBy(i => i.Id)
            : q.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id);

        var items = await q
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<IncidentResponse>(
            items.Select(ToResponse).ToList(), query.Page, query.PageSize, total);
    }

    /// <summary>
    /// BR-BIZ-09. Bản ghi tồn tại nhưng không thuộc quyền thì trả 403 chứ không phải 404 —
    /// hợp đồng mã lỗi mục 3.3 chọn nói thật để thông báo lỗi còn dạy được cho người dùng.
    /// </summary>
    public async Task<IncidentResponse> GetAsync(Guid id, ClaimsPrincipal caller, Guid? projectId, CancellationToken ct)
    {
        var incident = await BaseQuery().FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy sự cố '{id}'.");
        EnsureInScope(incident, projectId);

        await EnsureCanReadAsync(incident, caller);
        return ToResponse(incident);
    }

    /// <summary>
    /// FR-BIZ-09 — lịch sử theo thứ tự thời gian tăng dần. Cố ý bỏ qua query filter để sự cố
    /// đã xóa mềm vẫn tra cứu được bằng chứng SLA (GOAL-BIZ-02, ADR-003).
    /// </summary>
    public async Task<IReadOnlyList<StatusHistoryResponse>> GetHistoryAsync(
        Guid incidentId, ClaimsPrincipal caller, Guid? projectId, CancellationToken ct)
    {
        var incident = await _db.Incidents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy sự cố '{incidentId}'.");
        EnsureInScope(incident, projectId);

        // Lịch sử là dữ liệu của chính sự cố nên chịu đúng ràng buộc đọc như bản ghi cha;
        // bỏ sót chỗ này thì chặn được /incidents/{id} nhưng vẫn rò qua /history.
        await EnsureCanReadAsync(incident, caller);

        var rows = await _db.IncidentStatusHistory.AsNoTracking()
            .Include(h => h.ChangedByUser)
            .Where(h => h.IncidentId == incidentId)
            .OrderBy(h => h.ChangedAt).ThenBy(h => h.Id)
            .ToListAsync(ct);

        // Ghi chú chuyển trạng thái là chỗ nhân viên viết phân tích nội bộ — ô nhập không hề
        // báo nó sẽ hiện cho khách, nên phải coi là dữ liệu vận hành. Người báo cáo vẫn thấy đủ
        // mốc thời gian và ai đổi.
        // Lọc ở đây chứ không ở giao diện: lọc trên frontend thì nội dung vẫn nằm trong payload.
        //
        // Khoá theo `incident.note.read` chứ không phải `incident.read.all`: khách hàng sắp được
        // cấp quyền xem mọi sự cố, mà gộp hai điều đó vào một permission thì cấp quyền xem tổng
        // quan là vô tình mở luôn phần ghi chú nội bộ.
        var seesInternal = caller.HasPermission(Permissions.IncidentNoteRead);

        return rows.Select(h => new StatusHistoryResponse(
            h.Id, h.FromStatus, h.ToStatus, ToRef(h.ChangedByUser)!, h.ChangedAt,
            seesInternal ? h.Note : null)).ToList();
    }

    /// <summary>
    /// Người nhận được sự cố — cùng bộ quy tắc với <see cref="AssignAsync"/>, để ô chọn không bày
    /// ra những cái tên mà bấm vào sẽ nhận 409.
    ///
    /// Lọc theo permission chứ không theo tên vai trò: vai trò tự tạo có
    /// <c>incident.update_status</c> cũng nhận việc được, và không phải sửa chỗ này khi thêm vai
    /// trò mới.
    /// </summary>
    public async Task<IReadOnlyList<AssignableUser>> AssignableUsersAsync(string? search, CancellationToken ct)
    {
        var q = _db.Users.AsNoTracking()
            .Where(u => u.IsActive
                && u.UserRoles.Any(ur => ur.Role.RolePermissions
                    .Any(rp => rp.Permission.Code == Permissions.IncidentUpdateStatus)));

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            q = q.Where(u => u.Login.Contains(term) || u.DisplayName.ToLower().Contains(term));
        }

        return await q.OrderBy(u => u.DisplayName).Take(50)
            .Select(u => new AssignableUser(u.Id, u.Login, u.DisplayName))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Sự cố này có nằm trong project trên đường dẫn không.
    ///
    /// Không có bước này thì cách ly hở một đường hiển nhiên: biết id của một sự cố ở project
    /// khác rồi ghép vào slug mình có quyền là đọc được. Trả **404 chứ không 403** — 403 xác nhận
    /// sự cố đó có thật, tức vẫn rò một bit thông tin.
    /// </summary>
    private static void EnsureInScope(Incident incident, Guid? projectId)
    {
        if (incident.ProjectId != projectId)
        {
            throw AppException.NotFound($"Không tìm thấy sự cố '{incident.Id}' trong project này.");
        }
    }

    /// <summary>
    /// Chuyển sự cố sang project khác.
    ///
    /// <b>Kéo theo cả phản hồi đã gắn vào nó.</b> Có một bất biến phải giữ: phản hồi và sự cố nó
    /// gắn vào luôn cùng project. Chuyển sự cố mà bỏ lại phản hồi thì mở phản hồi ở project A lại
    /// thấy nó trỏ sang sự cố của project B — và không màn hình nào biết phải hiển thị nó ở đâu.
    /// </summary>
    public async Task<IncidentResponse> TransferAsync(
        Guid incidentId, Guid? fromProjectId, Guid? toProjectId, Guid actorId, CancellationToken ct)
    {
        var incident = await _db.Incidents.FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy sự cố '{incidentId}'.");
        EnsureInScope(incident, fromProjectId);

        if (incident.ProjectId == toProjectId)
        {
            return ToResponse(await BaseQuery().FirstAsync(i => i.Id == incidentId, ct));
        }

        incident.ProjectId = toProjectId;

        var linked = await _db.Feedbacks.Where(f => f.IncidentId == incidentId).ToListAsync(ct);
        foreach (var f in linked) f.ProjectId = toProjectId;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Audit incident.transfer actor={ActorId} incident={IncidentId} to={ProjectId} feedbacks={Count}",
            actorId, incidentId, toProjectId, linked.Count);

        return ToResponse(await BaseQuery().FirstAsync(i => i.Id == incidentId, ct));
    }

    // ---------------- Helpers ----------------

    /// <summary>
    /// Một cửa duy nhất cho mọi đường đọc chi tiết sự cố. Quy tắc nằm trong handler, hàm này
    /// chỉ dịch kết quả sang lỗi HTTP.
    /// </summary>
    private async Task EnsureCanReadAsync(Incident incident, ClaimsPrincipal caller)
    {
        var result = await _authorization.AuthorizeAsync(
            caller, incident, ResourceOperationRequirement.Read);

        if (!result.Succeeded)
        {
            throw new AppException(
                StatusCodes.Status403Forbidden,
                "Không đủ quyền",
                "Sự cố này không do bạn báo cáo và cũng không được giao cho bạn.",
                new Dictionary<string, object?>
                {
                    ["requiredPermission"] = Permissions.IncidentReadAll
                });
        }
    }

    private IQueryable<Incident> BaseQuery()
        => _db.Incidents.AsNoTracking()
            .Include(i => i.Reporter)
            .Include(i => i.Assignee)
            .Include(i => i.Resolver);

    /// <summary>
    /// Khóa hàng trong phạm vi transaction hiện hành. Bỏ qua query filter để sự cố đã xóa mềm
    /// vẫn trả 409 thay vì 404 khi có ai đó cố chuyển trạng thái nó.
    /// </summary>
    private async Task<Incident?> LockAsync(Guid id, CancellationToken ct)
    {
        var rows = await _db.Incidents
            .FromSql($"SELECT * FROM incidents WHERE id = {id} FOR UPDATE")
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        return rows.FirstOrDefault();
    }

    private async Task<IncidentResponse> LoadResponseAsync(Guid id, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var incident = await BaseQuery().FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy sự cố '{id}'.");
        return ToResponse(incident);
    }

    private IncidentResponse ToResponse(Incident i)
        => new(
            i.Id, i.Title, i.Description, i.Severity, i.Status,
            IncidentStateMachine.AllowedNext(i.Status),
            ToRef(i.Reporter)!, ToRef(i.Assignee),
            i.CreatedAt, i.MitigatingAt, i.ResolvedAt, ToRef(i.Resolver), i.IsDeleted,
            SlaEvaluator.Evaluate(_sla.CurrentValue, i.Status, i.CreatedAt, i.MitigatingAt,
                _clock.GetUtcNow()));

    private static UserRef? ToRef(User? user)
        => user is null ? null : new UserRef(user.Id, user.DisplayName, user.Email);
}
