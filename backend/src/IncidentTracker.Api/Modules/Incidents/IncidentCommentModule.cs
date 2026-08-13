using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Revisions;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Incidents;

// ---------------- DTO ----------------

public sealed class CreateIncidentCommentRequest
{
    [Required, MinLength(1), MaxLength(2000)]
    public string Body { get; set; } = string.Empty;
}

/// <summary>Sửa một bình luận. Body bắt buộc — đây là PUT nội dung, không phải PATCH từng phần.</summary>
public sealed class UpdateIncidentCommentRequest
{
    [Required, MinLength(1), MaxLength(2000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>Lý do sửa — nên điền khi sửa bình luận của người khác.</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }
}

public sealed record IncidentCommentResponse(
    Guid Id,
    Guid IncidentId,
    UserRef Author,
    string Body,
    DateTimeOffset CreatedAt,
    /// <summary>null khi bình luận còn nguyên bản; lịch sử đầy đủ ở <c>{commentId}/revisions</c>.</summary>
    EditSignature? LastEdit,
    /// <summary>Phiên bản nội dung — đặt vào <c>If-Match: "v{version}"</c> khi lưu.</summary>
    int Version);

// ---------------- Service ----------------

/// <summary>
/// UC-BIZ-09 — kênh trao đổi hai chiều trên một sự cố. Ranh giới đọc/ghi đi theo đúng ranh
/// giới đọc của sự cố cha (BR-BIZ-09): ai đọc được sự cố thì đọc được hội thoại, ai đọc được
/// và có <c>incident.comment</c> thì được nói. Không có quy tắc riêng nào để lệch được.
/// </summary>
public sealed class IncidentCommentService
{
    private readonly AppDbContext _db;
    private readonly IAuthorizationService _authorization;
    private readonly ContentRevisionService _revisions;
    private readonly EditClaimService _claims;
    private readonly ILogger<IncidentCommentService> _logger;

    public IncidentCommentService(AppDbContext db, IAuthorizationService authorization,
        ContentRevisionService revisions, EditClaimService claims,
        ILogger<IncidentCommentService> logger)
    {
        _db = db;
        _authorization = authorization;
        _revisions = revisions;
        _claims = claims;
        _logger = logger;
    }

    /// <summary>
    /// Hội thoại theo thứ tự thời gian tăng dần. Cùng quyết định với lịch sử trạng thái
    /// (ADR-003): sự cố đã xóa mềm vẫn đọc được hội thoại để giữ bằng chứng.
    /// </summary>
    public async Task<IReadOnlyList<IncidentCommentResponse>> ListAsync(
        Guid incidentId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var incident = await FindAsync(incidentId, ct);
        await EnsureCanReadAsync(incident, caller);

        var rows = await _db.IncidentComments.AsNoTracking()
            .Include(c => c.Author)
            .Include(c => c.LastEditor)
            .Where(c => c.IncidentId == incidentId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync(ct);

        return rows.Select(ToResponse).ToList();
    }

    /// <summary>
    /// Viết bình luận. Cho phép cả trên sự cố đã <c>Resolved</c> — lời giải thích sau khi đóng
    /// là một phần bình thường của hội thoại; chỉ sự cố đã xóa mềm mới đóng miệng (409).
    /// </summary>
    public async Task<IncidentCommentResponse> CreateAsync(
        Guid incidentId, CreateIncidentCommentRequest request, ClaimsPrincipal caller, CancellationToken ct)
    {
        var actorId = caller.GetUserId();

        var incident = await FindAsync(incidentId, ct);
        await EnsureCanReadAsync(incident, caller);

        if (incident.IsDeleted)
        {
            throw AppException.Conflict("Sự cố đã bị xóa mềm nên không nhận thêm trao đổi.");
        }

        var comment = new IncidentComment
        {
            Id = Guid.NewGuid(),
            IncidentId = incidentId,
            AuthorId = actorId,
            Body = request.Body.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.IncidentComments.Add(comment);
        await _db.SaveChangesAsync(ct);

        // NFR-SEC-02: không ghi body vào log — nội dung trao đổi có thể chứa PII.
        _logger.LogInformation(
            "Audit incident.comment actor={ActorId} incident={IncidentId} comment={CommentId} result=success",
            actorId, incidentId, comment.Id);

        var author = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == actorId, ct);
        comment.Author = author;
        return ToResponse(comment);
    }


    /// <summary>
    /// Sửa nội dung một bình luận — tác giả của nó, hoặc người có <c>incident.manage_any</c>.
    ///
    /// Lời cũ không mất: nó nằm lại trong <c>content_revisions</c> kèm tên người đã thay nó.
    /// Đó là điều kiện để cho sửa được mà hội thoại vẫn còn là bằng chứng — ai đọc sau cũng
    /// dựng lại được nguyên văn từng câu đã nói, và biết ai đổi câu nào.
    /// </summary>
    public async Task<IncidentCommentResponse> UpdateAsync(
        Guid incidentId, Guid commentId, UpdateIncidentCommentRequest request,
        ClaimsPrincipal caller, HttpRequest http, CancellationToken ct)
    {
        var actorId = caller.GetUserId();
        var body = request.Body.Trim();

        if (body.Length == 0)
        {
            throw AppException.BadRequest("Nội dung bình luận không được rỗng.");
        }

        var incident = await FindAsync(incidentId, ct);
        await EnsureCanReadAsync(incident, caller);

        var strategy = _db.Database.CreateExecutionStrategy();
        var changed = false;

        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct);

            // Khóa hàng trong transaction: hai người sửa cùng lúc thì người sau đọc được nội
            // dung người trước vừa ghi, nên old_value trong lịch sử luôn là giá trị thật ngay
            // trước đó chứ không phải một bản chụp đã cũ.
            var comment = (await _db.IncidentComments
                    .FromSql($"SELECT * FROM incident_comments WHERE id = {commentId} FOR UPDATE")
                    .ToListAsync(ct))
                .FirstOrDefault();

            if (comment is null || comment.IncidentId != incidentId)
            {
                throw AppException.NotFound($"Không tìm thấy bình luận '{commentId}'.");
            }

            EnsureCanEdit(comment, incident, caller);

            // Cửa chiếm dụng — xem IncidentService.UpdateContentAsync. Đặt sau khi đã khoá hàng.
            await _claims.EnsureWritableAsync(http, EditableEntityType.IncidentComment, comment.Id,
                comment.Version, actorId, "Bình luận", ct);

            var edit = _revisions.Begin(
                EditableEntityType.IncidentComment, comment.Id, comment,
                actorId, comment.AuthorId, request.Reason);

            edit.Change("body", comment.Body, body);
            comment.Body = body;

            changed = edit.Record();
            if (changed)
            {
                await _db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
            return true;
        });

        if (changed)
        {
            // NFR-SEC-02: không ghi body vào log — nội dung trao đổi có thể chứa PII.
            _logger.LogInformation(
                "Audit incident.comment.edit actor={ActorId} incident={IncidentId} comment={CommentId} result=success",
                actorId, incidentId, commentId);
        }

        _db.ChangeTracker.Clear();
        var updated = await _db.IncidentComments.AsNoTracking()
            .Include(c => c.Author)
            .Include(c => c.LastEditor)
            .FirstAsync(c => c.Id == commentId, ct);

        return ToResponse(updated);
    }

    /// <summary>
    /// Lịch sử sửa của một bình luận. Ràng buộc đọc đi theo sự cố cha, hệt như chính bình
    /// luận đó — chặn được <c>/comments</c> mà để hở <c>/revisions</c> thì lời cũ vẫn rò ra.
    /// </summary>
    public async Task<IReadOnlyList<RevisionResponse>> GetRevisionsAsync(
        Guid incidentId, Guid commentId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var incident = await FindAsync(incidentId, ct);
        await EnsureCanReadAsync(incident, caller);

        var exists = await _db.IncidentComments.AsNoTracking()
            .AnyAsync(c => c.Id == commentId && c.IncidentId == incidentId, ct);

        if (!exists)
        {
            throw AppException.NotFound($"Không tìm thấy bình luận '{commentId}'.");
        }

        return await _revisions.ListAsync(EditableEntityType.IncidentComment, commentId, ct);
    }


    /// <summary>Nhận hoặc gia hạn chỗ sửa trên một bình luận — cùng điều kiện với đường PATCH.</summary>
    public async Task<EditClaimResponse> ClaimEditAsync(
        Guid incidentId, Guid commentId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var incident = await FindAsync(incidentId, ct);
        await EnsureCanReadAsync(incident, caller);

        var comment = await _db.IncidentComments.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == commentId && c.IncidentId == incidentId, ct)
            ?? throw AppException.NotFound($"Không tìm thấy bình luận '{commentId}'.");

        EnsureCanEdit(comment, incident, caller);

        return await _claims.ClaimAsync(
            EditableEntityType.IncidentComment, commentId, comment.Version, caller.GetUserId(), ct);
    }

    /// <summary>Nhả chỗ sửa — chỉ xoá hàng của chính người gọi, nên không đòi quyền sửa.</summary>
    public async Task ReleaseEditAsync(
        Guid incidentId, Guid commentId, ClaimsPrincipal caller, CancellationToken ct)
    {
        var incident = await FindAsync(incidentId, ct);
        await EnsureCanReadAsync(incident, caller);

        await _claims.ReleaseAsync(
            EditableEntityType.IncidentComment, commentId, caller.GetUserId(), ct);
    }

    /// <summary>
    /// Một cửa duy nhất cho câu hỏi "người này sửa được bình luận này không" — đường PATCH và
    /// đường giữ chỗ phải hẹp bằng nhau, nếu không thì chiếm được chỗ mà không ghi được.
    /// </summary>
    private static void EnsureCanEdit(IncidentComment comment, Incident incident, ClaimsPrincipal caller)
    {
        if (!ResourceAccessRules.CanEditIncidentComment(caller, comment.AuthorId))
        {
            throw new AppException(
                StatusCodes.Status403Forbidden,
                "Không đủ quyền",
                "Chỉ tác giả bình luận mới sửa được, hoặc cần permission "
                + $"'{Permissions.IncidentManageAny}'.",
                new Dictionary<string, object?>
                {
                    ["requiredPermission"] = Permissions.IncidentManageAny
                });
        }

        // Cùng ràng buộc với việc viết mới: sự cố đã xóa mềm thì hội thoại đóng lại, đọc được
        // nhưng không đổi được nữa.
        if (incident.IsDeleted)
        {
            throw AppException.Conflict("Sự cố đã bị xóa mềm nên hội thoại không sửa được nữa.");
        }
    }

    private async Task<Incident> FindAsync(Guid incidentId, CancellationToken ct)
        => await _db.Incidents.AsNoTracking().IgnoreQueryFilters()
               .FirstOrDefaultAsync(i => i.Id == incidentId, ct)
           ?? throw AppException.NotFound($"Không tìm thấy sự cố '{incidentId}'.");

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

    private static IncidentCommentResponse ToResponse(IncidentComment c)
        => new(c.Id, c.IncidentId,
            new UserRef(c.Author.Id, c.Author.DisplayName, c.Author.Email),
            c.Body, c.CreatedAt,
            ContentRevisionService.SignatureOf(c.LastEditedAt, c.LastEditor),
            c.Version);
}

// ---------------- Controller ----------------

/// <summary>API-Incident-Comments — trao đổi hai chiều trên một sự cố (UC-BIZ-09).</summary>
[ApiController]
[Route("api/projects/{project}/incidents/{id:guid}/comments")]
[Produces("application/json")]
public sealed class IncidentCommentsController : ControllerBase
{
    private readonly IncidentCommentService _comments;
    private readonly Persistence.AppDbContext _db;

    public IncidentCommentsController(IncidentCommentService comments, Persistence.AppDbContext db)
    {
        _comments = comments;
        _db = db;
    }

    /// <summary>
    /// Bình luận là dữ liệu của sự cố, nên phải chịu đúng ràng buộc project như bản ghi cha.
    /// Bỏ sót chỗ này thì chặn được <c>/incidents/{id}</c> mà vẫn rò qua <c>/comments</c>.
    /// </summary>
    private async Task EnsureInScopeAsync(string project, Guid id, CancellationToken ct)
    {
        var projectId = await Tickets.Organization.VirtualProject.ResolveAsync(_db, project, User, ct);
        var actual = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            _db.Incidents.IgnoreQueryFilters().Where(i => i.Id == id).Select(i => new { i.ProjectId }), ct);

        if (actual is null || actual.ProjectId != projectId)
        {
            throw Common.AppException.NotFound($"Không tìm thấy sự cố '{id}' trong project này.");
        }
    }

    [HttpGet]
    [RequirePermission(Permissions.IncidentRead)]
    [ProducesResponseType(typeof(IReadOnlyList<IncidentCommentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<IncidentCommentResponse>>> List(
        string project, Guid id, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _comments.ListAsync(id, User, ct));
    }

    [HttpPost]
    [RequirePermission(Permissions.IncidentComment)]
    [ProducesResponseType(typeof(IncidentCommentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IncidentCommentResponse>> Create(
        string project, Guid id, CreateIncidentCommentRequest request, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        var created = await _comments.CreateAsync(id, request, User, ct);
        return CreatedAtAction(nameof(List), new { project, id }, created);
    }

    /// <summary>API-Incident-Comment-Update — sửa bình luận, lời cũ ở lại trong lịch sử.</summary>
    [HttpPatch("{commentId:guid}")]
    [RequirePermission(Permissions.IncidentComment)]
    [ProducesResponseType(typeof(IncidentCommentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IncidentCommentResponse>> Update(
        string project, Guid id, Guid commentId, UpdateIncidentCommentRequest request, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _comments.UpdateAsync(id, commentId, request, User, Request, ct));
    }

    /// <summary>API-Incident-Comment-EditClaim — "tôi đang mở form sửa bình luận này".</summary>
    [HttpPut("{commentId:guid}/edit-claim")]
    [RequirePermission(Permissions.IncidentComment)]
    [ProducesResponseType(typeof(EditClaimResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EditClaimResponse>> ClaimEdit(
        string project, Guid id, Guid commentId, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _comments.ClaimEditAsync(id, commentId, User, ct));
    }

    /// <summary>API-Incident-Comment-EditClaim-Release.</summary>
    [HttpDelete("{commentId:guid}/edit-claim")]
    [RequirePermission(Permissions.IncidentComment)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReleaseEdit(
        string project, Guid id, Guid commentId, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        await _comments.ReleaseEditAsync(id, commentId, User, ct);
        return NoContent();
    }

    /// <summary>API-Incident-Comment-Revisions.</summary>
    [HttpGet("{commentId:guid}/revisions")]
    [RequirePermission(Permissions.IncidentRead)]
    [ProducesResponseType(typeof(IReadOnlyList<RevisionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RevisionResponse>>> Revisions(
        string project, Guid id, Guid commentId, CancellationToken ct)
    {
        await EnsureInScopeAsync(project, id, ct);
        return Ok(await _comments.GetRevisionsAsync(id, commentId, User, ct));
    }
}
