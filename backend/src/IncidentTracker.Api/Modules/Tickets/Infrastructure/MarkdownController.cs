using System.ComponentModel.DataAnnotations;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IncidentTracker.Api.Modules.Tickets.Infrastructure;

public sealed class MarkdownPreviewRequest
{
    [MaxLength(65536)] public string? Text { get; set; }
    [MaxLength(100)] public string? Project { get; set; }
}

/// <summary>
/// Tab "Preview" của ô soạn (≈ <c>POST /markdown</c> của GitHub): render + sanitize giống hệt đường
/// hiển thị thật (BR-SEC-02) để người dùng thấy đúng những gì sẽ được lưu/hiển thị.
/// </summary>
[ApiController]
[Route("api/markdown")]
[Produces("application/json")]
public sealed class MarkdownController : ControllerBase
{
    private readonly MarkdownRenderer _renderer;
    private readonly TicketQueries _q;

    public MarkdownController(MarkdownRenderer renderer, TicketQueries q)
    {
        _renderer = renderer;
        _q = q;
    }

    [HttpPost("preview")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<object>> Preview(MarkdownPreviewRequest request, CancellationToken ct)
    {
        var known = await _q.KnownLoginsAsync(request.Text, ct);
        var rendered = _renderer.Render(request.Text, request.Project ?? "support", known);
        return Ok(new { html = rendered.Html, mentions = rendered.Mentions, references = rendered.References });
    }
}
