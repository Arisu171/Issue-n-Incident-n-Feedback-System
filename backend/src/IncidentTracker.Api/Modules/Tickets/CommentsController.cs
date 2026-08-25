using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IncidentTracker.Api.Modules.Tickets;

/// <summary>Comment, internal note, kiểm duyệt comment (mục 6.5, UC-02/03/18).</summary>
[ApiController]
[Route("api/projects/{project}/tickets/{number:int}")]
[Produces("application/json")]
public sealed class CommentsController : ControllerBase
{
    private readonly CommentService _comments;

    public CommentsController(CommentService comments) => _comments = comments;

    [HttpGet("comments")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    [ProducesResponseType(typeof(CursorPage<CommentResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<CursorPage<CommentResponse>>> List(string project, int number,
        [FromQuery] string? cursor, [FromQuery(Name = "per_page")] int? perPage, CancellationToken ct)
        => Ok(await _comments.ListAsync(project, number, cursor, perPage, User, ct));

    /// <summary>Bình luận (bắt buộc Idempotency-Key). Ticket khoá → chỉ Write+ (BR-LIFECYCLE-02).</summary>
    [HttpPost("comments")]
    [RequirePermission(Permissions.TicketComment)]
    [RequireIdempotencyKey]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(typeof(CommentResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<CommentResponse>> Create(string project, int number, CommentRequest request, CancellationToken ct)
    {
        var created = await _comments.CreateAsync(project, number, request, User, ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    /// <summary>Mở rộng UC-03: ghi chú nội bộ, visibility INTERNAL.</summary>
    [HttpPost("internal-notes")]
    [RequirePermission(Permissions.TicketInternalNote)]
    [RequireIdempotencyKey]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    [ProducesResponseType(typeof(CommentResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<CommentResponse>> InternalNote(string project, int number, CommentRequest request, CancellationToken ct)
    {
        var created = await _comments.InternalNoteAsync(project, number, request, User, ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("comments/{commentId:guid}")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<CommentResponse>> Edit(string project, int number, Guid commentId, CommentRequest request, CancellationToken ct)
        => Ok(await _comments.EditAsync(project, number, commentId, request, User, ct));

    [HttpDelete("comments/{commentId:guid}")]
    [RequirePermission(Permissions.TicketComment)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> Delete(string project, int number, Guid commentId, CancellationToken ct)
    {
        await _comments.DeleteAsync(project, number, commentId, User, ct);
        return NoContent();
    }

    /// <summary>Ẩn (minimize) comment kèm lý do — Write+.</summary>
    [HttpPut("comments/{commentId:guid}/minimize")]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<CommentResponse>> Minimize(string project, int number, Guid commentId, MinimizeRequest request, CancellationToken ct)
        => Ok(await _comments.MinimizeAsync(project, number, commentId, request.Reason, true, User, ct));

    [HttpDelete("comments/{commentId:guid}/minimize")]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<CommentResponse>> Unminimize(string project, int number, Guid commentId, CancellationToken ct)
        => Ok(await _comments.MinimizeAsync(project, number, commentId, null, false, User, ct));

    /// <summary>Lịch sử sửa (BR-EDIT-01).</summary>
    [HttpGet("comments/{commentId:guid}/edits")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<CommentEditResponse>>> Edits(string project, int number, Guid commentId, CancellationToken ct)
        => Ok(await _comments.EditsAsync(project, number, commentId, User, ct));

    [HttpDelete("comments/{commentId:guid}/edits/{revisionId:guid}")]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> DeleteEdit(string project, int number, Guid commentId, Guid revisionId, CancellationToken ct)
    {
        await _comments.DeleteEditAsync(project, number, commentId, revisionId, User, ct);
        return NoContent();
    }
}
