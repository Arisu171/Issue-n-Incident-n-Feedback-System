using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
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

public sealed record IncidentCommentResponse(
    Guid Id,
    Guid IncidentId,
    UserRef Author,
    string Body,
    DateTimeOffset CreatedAt);

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
    private readonly ILogger<IncidentCommentService> _logger;

    public IncidentCommentService(AppDbContext db, IAuthorizationService authorization,
        ILogger<IncidentCommentService> logger)
    {
        _db = db;
        _authorization = authorization;
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
            c.Body, c.CreatedAt);
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
}
