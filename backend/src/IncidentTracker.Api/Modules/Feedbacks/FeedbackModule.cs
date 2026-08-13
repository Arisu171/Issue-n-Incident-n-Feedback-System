using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using IncidentTracker.Api.Modules.Incidents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IncidentTracker.Api.Modules.Feedbacks;

// ---------------- DTO ----------------

public sealed class CreateFeedbackRequest
{
    [Required]
    public FeedbackChannel Channel { get; set; }

    [EmailAddress, MaxLength(320)]
    public string? CustomerEmail { get; set; }

    [Required, MinLength(10), MaxLength(10_000)]
    public string Content { get; set; } = string.Empty;

    /// <summary>Gắn ngay vào một sự cố đang mở. Bỏ trống thì feedback nằm ở hàng đợi chưa phân loại.</summary>
    public Guid? IncidentId { get; set; }
}

public sealed class LinkFeedbackRequest
{
    [Required]
    public Guid IncidentId { get; set; }
}

public sealed record FeedbackResponse(
    Guid Id,
    FeedbackChannel Channel,
    string? CustomerEmail,
    string Content,
    FeedbackStatus Status,
    Guid? IncidentId,
    string? IncidentTitle,
    IncidentStatus? IncidentStatus,
    Guid CreatedBy,
    string CreatedByName,
    DateTimeOffset CreatedAt);

public sealed class CreateFeedbackReplyRequest
{
    [Required, MinLength(1), MaxLength(2000)]
    public string Body { get; set; } = string.Empty;
}

/// <summary><c>Responder</c> null khi <c>IsAutomatic</c> — lời xác nhận do hệ thống sinh.</summary>
public sealed record FeedbackReplyResponse(
    Guid Id,
    Guid FeedbackId,
    UserRef? Responder,
    bool IsAutomatic,
    string Body,
    DateTimeOffset CreatedAt);

/// <summary>
/// Bổ sung so với mục 6.6: không có endpoint danh sách thì partial index
/// <c>where incident_id is null</c> (mục 5.6) và hàng đợi ~100 phản hồi tồn đọng của
/// Support không dùng được.
/// </summary>
public sealed class FeedbackListQuery : PagingQuery
{
    /// <summary>Tìm chữ trong nội dung phản hồi và email khách hàng.</summary>
    [MaxLength(200)]
    public string? Q { get; set; }

    /// <summary>true = chỉ hàng đợi chưa phân loại; false = chỉ feedback đã gắn; bỏ trống = tất cả.</summary>
    public bool? UnlinkedOnly { get; set; }

    public Guid? IncidentId { get; set; }
}

// ---------------- Service ----------------

/// <summary>CMP-09 Feedback Service — FR-BIZ-07, BR-BIZ-07.</summary>
public sealed class FeedbackService
{
    private readonly AppDbContext _db;
    private readonly IAuthorizationService _authorization;
    private readonly IOptionsMonitor<FeedbackOptions> _options;
    private readonly ILogger<FeedbackService> _logger;

    public FeedbackService(AppDbContext db, IAuthorizationService authorization,
        IOptionsMonitor<FeedbackOptions> options, ILogger<FeedbackService> logger)
    {
        _db = db;
        _authorization = authorization;
        _options = options;
        _logger = logger;
    }

    public async Task<FeedbackResponse> CreateAsync(
        CreateFeedbackRequest request, ClaimsPrincipal caller, Guid? projectId, CancellationToken ct)
    {
        var actorId = caller.GetUserId();

        if (request.IncidentId is not null)
        {
            await EnsureLinkableAsync(request.IncidentId.Value, projectId, caller, ct);
        }

        var feedback = new Feedback
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Channel = request.Channel,
            CustomerEmail = string.IsNullOrWhiteSpace(request.CustomerEmail)
                ? null
                : request.CustomerEmail.Trim().ToLowerInvariant(),
            Content = request.Content.Trim(),
            // Gắn ngay vào sự cố lúc tạo cũng là một hình thức tiếp nhận (BR-BIZ-12).
            Status = request.IncidentId is null ? FeedbackStatus.New : FeedbackStatus.Acknowledged,
            IncidentId = request.IncidentId,
            CreatedBy = actorId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Feedbacks.Add(feedback);

        // Phản hồi tự động (FR-BIZ-14): xác nhận tiếp nhận nằm ngay trong luồng hội thoại,
        // ghi cùng một SaveChanges với feedback nên không bao giờ có feedback "mồ côi" ack.
        var autoAck = _options.CurrentValue;
        if (autoAck.AutoAckEnabled)
        {
            _db.FeedbackReplies.Add(new FeedbackReply
            {
                Id = Guid.NewGuid(),
                FeedbackId = feedback.Id,
                ResponderId = null,
                IsAutomatic = true,
                Body = autoAck.AutoAckMessage,
                CreatedAt = feedback.CreatedAt
            });
        }

        await _db.SaveChangesAsync(ct);

        // NFR-SEC-02 / PII: chỉ ghi id và kênh, không bao giờ ghi content hay customer_email.
        _logger.LogInformation("Audit feedback.create actor={ActorId} feedback={FeedbackId} channel={Channel} linked={Linked} autoAck={AutoAck} result=success",
            actorId, feedback.Id, feedback.Channel, feedback.IncidentId is not null, autoAck.AutoAckEnabled);

        return await LoadAsync(feedback.Id, ct);
    }

    /// <summary>
    /// API-Feedback-Link · BR-BIZ-07. Idempotent với cùng incidentId; gắn vào sự cố đã
    /// <c>Resolved</c> trả 409 và giữ nguyên trạng thái cũ của feedback (US-BIZ-04/AC-02).
    ///
    /// Ranh giới đọc được soát ở <b>cả hai</b> đầu: phản hồi phải là của mình (trừ khi có
    /// <c>feedback.read.all</c>) và sự cố phải là sự cố mình đọc được. Thiếu vế thứ nhất thì
    /// bất kỳ ai cầm <c>feedback.link</c> cũng gắn được phản hồi của người khác vào sự cố của
    /// mình, và câu trả lời của endpoint sẽ trả nguyên nội dung phản hồi đó về (BR-SEC-01).
    /// </summary>
    public async Task<FeedbackResponse> LinkAsync(
        Guid feedbackId, Guid incidentId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var actorId = caller.GetUserId();

        var feedback = await _db.Feedbacks.FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        // 404 chứ không 403: 403 xác nhận rằng phản hồi đó có thật, tức vẫn rò một bit thông tin.
        if (!ResourceAccessRules.CanReadFeedback(caller, feedback.CreatedBy))
        {
            throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");
        }

        if (feedback.IncidentId == incidentId)
        {
            return await LoadAsync(feedbackId, ct);
        }

        await EnsureLinkableAsync(incidentId, feedback.ProjectId, caller, ct);

        feedback.IncidentId = incidentId;

        // BR-BIZ-12: gắn vào sự cố nghĩa là đã có người tiếp nhận. Không hạ cấp
        // Responded — câu trả lời của con người là mốc cao hơn việc phân loại.
        if (feedback.Status == FeedbackStatus.New)
        {
            feedback.Status = FeedbackStatus.Acknowledged;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Audit feedback.link actor={ActorId} feedback={FeedbackId} incident={IncidentId} result=success",
            actorId, feedbackId, incidentId);

        return await LoadAsync(feedbackId, ct);
    }

    // ---------------- UC-BIZ-09 · Trả lời phản hồi ----------------

    /// <summary>
    /// Luồng trả lời của một phản hồi, thứ tự thời gian tăng dần — gồm cả lời xác nhận
    /// tự động. Ranh giới đọc đi theo feedback cha (BR-BIZ-11).
    /// </summary>
    public async Task<IReadOnlyList<FeedbackReplyResponse>> ListRepliesAsync(
        Guid feedbackId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var feedback = await _db.Feedbacks.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        await EnsureAsync(feedback, caller, ResourceOperationRequirement.Read,
            "Phản hồi này không do bạn gửi.");

        var rows = await _db.FeedbackReplies.AsNoTracking()
            .Include(r => r.Responder)
            .Where(r => r.FeedbackId == feedbackId)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToListAsync(ct);

        return rows.Select(ToReplyResponse).ToList();
    }

    /// <summary>
    /// FR-BIZ-14 — doanh nghiệp trả lời phản hồi và feedback chuyển sang <c>Responded</c>.
    /// Trả lời tiếp một feedback đã <c>Responded</c> vẫn hợp lệ: hội thoại không đóng.
    /// </summary>
    public async Task<FeedbackReplyResponse> ReplyAsync(
        Guid feedbackId, CreateFeedbackReplyRequest request, ClaimsPrincipal caller, CancellationToken ct)
    {
        var actorId = caller.GetUserId();

        var feedback = await _db.Feedbacks.FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        await EnsureAsync(feedback, caller, ResourceOperationRequirement.Write,
            "Phản hồi này nằm ngoài phạm vi bạn được trả lời.");

        var reply = new FeedbackReply
        {
            Id = Guid.NewGuid(),
            FeedbackId = feedbackId,
            ResponderId = actorId,
            IsAutomatic = false,
            Body = request.Body.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.FeedbackReplies.Add(reply);
        feedback.Status = FeedbackStatus.Responded;
        await _db.SaveChangesAsync(ct);

        // NFR-SEC-02: không ghi body vào log.
        _logger.LogInformation(
            "Audit feedback.respond actor={ActorId} feedback={FeedbackId} reply={ReplyId} result=success",
            actorId, feedbackId, reply.Id);

        var responder = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == actorId, ct);
        reply.Responder = responder;
        return ToReplyResponse(reply);
    }

    /// <summary>
    /// BR-BIZ-11 — người không có <c>feedback.read.all</c> chỉ thấy phản hồi do chính mình
    /// gửi. Nội dung phản hồi và email liên hệ đều là PII (mục 5.7) nên đây không chỉ là
    /// chuyện tiện dụng.
    /// </summary>
    public async Task<PagedResult<FeedbackResponse>> ListAsync(
        Guid? projectId, FeedbackListQuery query, ClaimsPrincipal caller, CancellationToken ct)
    {
        // Lọc theo project trong câu truy vấn, trước bộ lọc quyền sở hữu — lọc sau thì con số
        // tổng của phân trang đã tính trên dữ liệu project khác.
        var q = BaseQuery().Where(f => f.ProjectId == projectId);

        if (ResourceAccessRules.FeedbackVisibilityFilter(caller) is { } visibility)
        {
            q = q.Where(visibility);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var pattern = "%" + query.Q.Trim() + "%";
            q = q.Where(f => EF.Functions.ILike(f.Content, pattern)
                || (f.CustomerEmail != null && EF.Functions.ILike(f.CustomerEmail, pattern)));
        }

        if (query.UnlinkedOnly == true)
        {
            q = q.Where(f => f.IncidentId == null);
        }
        else if (query.UnlinkedOnly == false)
        {
            q = q.Where(f => f.IncidentId != null);
        }

        if (query.IncidentId is not null)
        {
            q = q.Where(f => f.IncidentId == query.IncidentId.Value);
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(f => f.CreatedAt).ThenBy(f => f.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<FeedbackResponse>(
            items.Select(ToResponse).ToList(), query.Page, query.PageSize, total);
    }

    public async Task<FeedbackResponse> GetAsync(Guid id, ClaimsPrincipal caller, CancellationToken ct)
    {
        var feedback = await BaseQuery().FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{id}'.");

        await EnsureAsync(feedback, caller, ResourceOperationRequirement.Read,
            "Phản hồi này không do bạn gửi.");

        return ToResponse(feedback);
    }

    /// <summary>Một cửa duy nhất dịch kết quả resource authorization sang lỗi HTTP 403.</summary>
    private async Task EnsureAsync(Feedback feedback, ClaimsPrincipal caller,
        ResourceOperationRequirement requirement, string detail)
    {
        var result = await _authorization.AuthorizeAsync(caller, feedback, requirement);

        if (!result.Succeeded)
        {
            throw new AppException(
                StatusCodes.Status403Forbidden,
                "Không đủ quyền",
                detail,
                new Dictionary<string, object?>
                {
                    ["requiredPermission"] = Permissions.FeedbackReadAll
                });
        }
    }

    /// <summary>
    /// BR-BIZ-07 — chỉ gắn được vào Incident tồn tại, chưa xóa mềm và chưa Resolved.
    ///
    /// Kèm cả ràng buộc đọc: nếu không kiểm, một khách hàng có thể dò id và móc phản hồi của
    /// mình vào sự cố của khách hàng khác — cùng họ lỗi BOLA, chỉ khác là ở chiều ghi.
    /// </summary>
    /// <summary>
    /// Chuyển phản hồi sang project khác.
    ///
    /// Phản hồi **đã gắn vào một sự cố** thì không chuyển riêng được: bất biến "phản hồi và sự cố
    /// cùng project" sẽ vỡ. Chuyển sự cố thì phản hồi đi theo — xem
    /// <c>IncidentService.TransferAsync</c>.
    /// </summary>
    public async Task<FeedbackResponse> TransferAsync(
        Guid feedbackId, Guid? fromProjectId, Guid? toProjectId, Guid actorId, CancellationToken ct)
    {
        var feedback = await _db.Feedbacks.FirstOrDefaultAsync(f => f.Id == feedbackId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}'.");

        if (feedback.ProjectId != fromProjectId)
        {
            throw AppException.NotFound($"Không tìm thấy phản hồi '{feedbackId}' trong project này.");
        }

        if (feedback.IncidentId is not null)
        {
            throw AppException.Conflict(
                "Phản hồi này đã gắn vào một sự cố nên đi theo sự cố đó. Chuyển sự cố thì phản hồi tự theo sang.");
        }

        feedback.ProjectId = toProjectId;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Audit feedback.transfer actor={ActorId} feedback={FeedbackId} to={ProjectId}",
            actorId, feedbackId, toProjectId);

        return ToResponse(await BaseQuery().FirstAsync(f => f.Id == feedbackId, ct));
    }

    private async Task EnsureLinkableAsync(
        Guid incidentId, Guid? feedbackProjectId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var incident = await _db.Incidents.AsNoTracking()
            .Select(i => new { i.Id, i.Status, i.ReporterId, i.AssigneeId, i.ProjectId })
            .FirstOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy sự cố '{incidentId}'.");

        // Phản hồi và sự cố nó gắn vào phải **cùng project**. Đây là bất biến, không phải sở
        // thích: mọi màn hình đều hỏi dữ liệu theo project, nên một phản hồi ở project A trỏ sang
        // sự cố ở project B là thứ không màn hình nào biết bày ở đâu — và
        // <c>TransferAsync</c> dựa hẳn vào nó để kéo phản hồi đi theo sự cố. Người có quyền toàn
        // cục chạm được cả hai project nên nếu không chặn ở đây thì chính họ làm vỡ bất biến.
        //
        // 404 chứ không 409, cùng lý do đã áp cho lỗ hổng BOLA: 409 xác nhận sự cố đó có thật.
        if (incident.ProjectId != feedbackProjectId)
        {
            throw AppException.NotFound($"Không tìm thấy sự cố '{incidentId}' trong project này.");
        }

        if (!ResourceAccessRules.CanAttachFeedbackTo(caller, incident.ReporterId, incident.AssigneeId))
        {
            throw AppException.Forbidden(
                "Sự cố này không do bạn báo cáo và cũng không được giao cho bạn nên không thể gắn phản hồi vào.");
        }

        if (incident.Status == Domain.IncidentStatus.Resolved)
        {
            throw AppException.Conflict(
                "Sự cố đã đóng nên không nhận thêm phản hồi. Hãy tạo sự cố mới hoặc để phản hồi ở hàng đợi chưa phân loại.",
                new Dictionary<string, object?> { ["currentStatus"] = incident.Status.ToString() });
        }
    }

    private IQueryable<Feedback> BaseQuery()
        => _db.Feedbacks.AsNoTracking()
            .Include(f => f.Incident)
            .Include(f => f.CreatedByUser);

    private async Task<FeedbackResponse> LoadAsync(Guid id, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var feedback = await BaseQuery().FirstOrDefaultAsync(f => f.Id == id, ct)
            ?? throw AppException.NotFound($"Không tìm thấy phản hồi '{id}'.");
        return ToResponse(feedback);
    }

    private static FeedbackResponse ToResponse(Feedback f)
        => new(f.Id, f.Channel, f.CustomerEmail, f.Content, f.Status, f.IncidentId,
            f.Incident?.Title, f.Incident?.Status,
            f.CreatedBy, f.CreatedByUser?.DisplayName ?? string.Empty, f.CreatedAt);

    private static FeedbackReplyResponse ToReplyResponse(FeedbackReply r)
        => new(r.Id, r.FeedbackId,
            r.Responder is null ? null : new UserRef(r.Responder.Id, r.Responder.DisplayName, r.Responder.Email),
            r.IsAutomatic, r.Body, r.CreatedAt);
}

// ---------------- Controller ----------------

/// <summary>CMP-07 Feedback Controller — UC-BIZ-04.</summary>
[ApiController]
[Route("api/projects/{project}/feedbacks")]
[Produces("application/json")]
public sealed class FeedbacksController : ControllerBase
{
    private readonly FeedbackService _feedbacks;
    private readonly Persistence.AppDbContext _db;

    public FeedbacksController(FeedbackService feedbacks, Persistence.AppDbContext db)
    {
        _feedbacks = feedbacks;
        _db = db;
    }

    private Task<Guid?> ScopeAsync(string project, CancellationToken ct)
        => Tickets.Organization.VirtualProject.ResolveAsync(_db, project, User, ct);

    /// <summary>
    /// Phản hồi này có nằm trong project trên đường dẫn không.
    ///
    /// 404 chứ không 403: 403 xác nhận phản hồi đó có thật, tức vẫn rò một bit thông tin — cùng
    /// lý do đã áp cho lỗ hổng BOLA ở <c>LinkAsync</c>.
    /// </summary>
    private async Task EnsureInScopeAsync(string project, Guid id, CancellationToken ct)
    {
        var projectId = await ScopeAsync(project, ct);
        var actual = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            _db.Feedbacks.Where(f => f.Id == id).Select(f => new { f.ProjectId }), ct);

        if (actual is null || actual.ProjectId != projectId)
        {
            throw Common.AppException.NotFound($"Không tìm thấy phản hồi '{id}' trong project này.");
        }
    }

    /// <summary>
    /// Hàng đợi phân loại của Support, đồng thời là danh sách "phản hồi của tôi" với khách
    /// hàng: người không có <c>feedback.read.all</c> chỉ nhận về phản hồi do chính mình gửi
    /// (BR-BIZ-11), nên PII của khách khác không bao giờ tới tay họ.
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(PagedResult<FeedbackResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<FeedbackResponse>>> List(
        string project, [FromQuery] FeedbackListQuery query, CancellationToken ct)
        => Ok(await _feedbacks.ListAsync(await ScopeAsync(project, ct), query, User, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeedbackResponse>> Get(string project, Guid id, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.GetAsync(id, User, ct));
    }

    /// <summary>API-Feedback-Create.</summary>
    [HttpPost]
    [RequirePermission(Permissions.FeedbackCreate)]
    [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeedbackResponse>> Create(
        string project, CreateFeedbackRequest request, CancellationToken ct)
    {
        var created = await _feedbacks.CreateAsync(request, User, await ScopeAsync(project, ct), ct);
        return CreatedAtAction(nameof(Get), new { project, id = created.Id }, created);
    }

    /// <summary>API-Feedback-Link.</summary>
    [HttpPost("{id:guid}/link")]
    [RequirePermission(Permissions.FeedbackLink)]
    [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeedbackResponse>> Link(
        string project, Guid id, LinkFeedbackRequest request, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.LinkAsync(id, request.IncidentId, User, ct));
    }

    /// <summary>Chuyển phản hồi sang project khác — thao tác phân loại.</summary>
    [HttpPost("{id:guid}/transfer")]
    [RequirePermission(Permissions.ProjectMemberManage)]
    [ProducesResponseType(typeof(FeedbackResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FeedbackResponse>> Transfer(
        string project, Guid id, Incidents.TransferProjectRequest request, CancellationToken ct)
    {
        var from = await ScopeAsync(project, ct);
        var to = await Tickets.Organization.VirtualProject.ResolveAsync(_db, request.ToProject, User, ct);
        return Ok(await _feedbacks.TransferAsync(id, from, to, User.GetUserId(), ct));
    }

    /// <summary>API-Feedback-Replies — luồng trả lời, gồm cả lời xác nhận tự động.</summary>
    [HttpGet("{id:guid}/replies")]
    [RequirePermission(Permissions.FeedbackRead)]
    [ProducesResponseType(typeof(IReadOnlyList<FeedbackReplyResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<FeedbackReplyResponse>>> Replies(
        string project, Guid id, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _feedbacks.ListRepliesAsync(id, User, ct));
    }

    /// <summary>API-Feedback-Respond · FR-BIZ-14 — doanh nghiệp trả lời khách hàng.</summary>
    [HttpPost("{id:guid}/replies")]
    [RequirePermission(Permissions.FeedbackRespond)]
    [ProducesResponseType(typeof(FeedbackReplyResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FeedbackReplyResponse>> Reply(
        string project, Guid id, CreateFeedbackReplyRequest request, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        var created = await _feedbacks.ReplyAsync(id, request, User, ct);
        return CreatedAtAction(nameof(Replies), new { project, id }, created);
    }
}
