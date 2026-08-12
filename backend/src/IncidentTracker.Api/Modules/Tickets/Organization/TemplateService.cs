using System.Security.Claims;
using System.Text;
using System.Text.Json;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Tickets.Organization;

/// <summary>
/// UC-17 / BR-TPL-01 / mục 5.5 (Architecture v3.1) — Issue Form: schema JSON tương đương YAML của
/// GitHub (<c>body[]</c> với markdown/input/textarea/dropdown/checkboxes + validations.required),
/// render câu trả lời thành Markdown theo thứ tự element, defaults tự gắn label/assignee/type/board.
/// </summary>
public sealed class TemplateService
{
    private static readonly HashSet<string> ElementTypes = new(StringComparer.Ordinal) { "markdown", "input", "textarea", "dropdown", "checkboxes" };

    private readonly AppDbContext _db;
    private readonly TicketQueries _q;

    public TemplateService(AppDbContext db, TicketQueries q)
    {
        _db = db;
        _q = q;
    }

    public async Task<IReadOnlyList<TemplateResponse>> ListAsync(string slug, bool includeDisabled, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var q = _db.TicketTemplates.AsNoTracking().Where(t => t.ProjectId == project.Id);
        if (!includeDisabled) q = q.Where(t => t.IsEnabled);
        var rows = await q.OrderBy(t => t.Position).ThenBy(t => t.Name).ToListAsync(ct);
        return rows.Select(t => ToResponse(t, project.Slug)).ToList();
    }

    public async Task<TemplateResponse> GetAsync(string slug, Guid id, CancellationToken ct)
    {
        var (project, template) = await FindAsync(slug, id, ct);
        return ToResponse(template, project.Slug);
    }

    public async Task<TemplateResponse> CreateAsync(string slug, TemplateRequest request, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        ValidateSchema(request.Body);
        var name = request.Name.Trim();
        if (await _db.TicketTemplates.AnyAsync(t => t.ProjectId == project.Id && t.Name == name, ct))
        {
            throw new AppException(StatusCodes.Status422UnprocessableEntity, "Template đã tồn tại", $"Template '{name}' đã có trong project.");
        }
        var template = new TicketTemplate
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Name = name, Description = request.Description?.Trim(),
            TitlePrefix = request.Title, Defaults = DefaultsJson(request), BodySchema = request.Body.GetRawText(),
            IsEnabled = request.IsEnabled ?? true, Position = request.Position ?? 0
        };
        _db.TicketTemplates.Add(template);
        await _db.SaveTranslatingConflictAsync("Template đã tồn tại.", ct);
        return ToResponse(template, project.Slug);
    }

    public async Task<TemplateResponse> UpdateAsync(string slug, Guid id, TemplateRequest request, CancellationToken ct)
    {
        var (project, template) = await FindAsync(slug, id, ct);
        ValidateSchema(request.Body);
        template.Name = request.Name.Trim();
        template.Description = request.Description?.Trim();
        template.TitlePrefix = request.Title;
        template.Defaults = DefaultsJson(request);
        template.BodySchema = request.Body.GetRawText();
        if (request.IsEnabled is { } e) template.IsEnabled = e;
        if (request.Position is { } p) template.Position = p;
        await _db.SaveTranslatingConflictAsync("Template đã tồn tại.", ct);
        return ToResponse(template, project.Slug);
    }

    public async Task DeleteAsync(string slug, Guid id, CancellationToken ct)
    {
        var (_, template) = await FindAsync(slug, id, ct);
        _db.TicketTemplates.Remove(template);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Điểm cắm vào <c>TicketService.CreateAsync</c>: render title/body + defaults từ câu trả lời.
    /// Thiếu trường <c>required</c> → 422 kèm <c>errors[]</c> (BR-TPL-01).
    /// </summary>
    public async Task<(string Title, string Body, TemplateDefaults? Defaults)> RenderAsync(Project project, CreateTicketRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        var template = await _db.TicketTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.ProjectId == project.Id && t.IsEnabled, ct)
            ?? throw AppException.NotFound("Không tìm thấy template hoặc template đã tắt.");

        var answers = request.FormAnswers ?? new Dictionary<string, JsonElement>();
        var errors = new List<object>();
        var body = new StringBuilder();
        using var schema = JsonDocument.Parse(template.BodySchema);

        foreach (var element in schema.RootElement.EnumerateArray())
        {
            var type = element.GetProperty("type").GetString()!;
            if (type == "markdown") continue; // chỉ hiển thị, không vào body

            var id = element.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
            var attributes = element.TryGetProperty("attributes", out var a) ? a : default;
            var label = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("label", out var l) ? l.GetString() ?? id : id;
            var required = element.TryGetProperty("validations", out var v) && v.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
            answers.TryGetValue(id, out var answer);

            switch (type)
            {
                case "input":
                case "textarea":
                {
                    var text = answer.ValueKind == JsonValueKind.String ? answer.GetString()!.Trim() : string.Empty;
                    if (text.Length == 0 && attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("value", out var def) && def.ValueKind == JsonValueKind.String)
                    {
                        text = def.GetString()!;
                    }
                    if (required && text.Length == 0) { errors.Add(new { field = id, code = "missing", message = $"'{label}' là bắt buộc." }); break; }
                    body.Append("### ").Append(label).Append("\n\n");
                    var render = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("render", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString() : null;
                    body.Append(render is null ? (text.Length == 0 ? "_No response_" : text) : $"```{render}\n{text}\n```").Append("\n\n");
                    break;
                }
                case "dropdown":
                {
                    var options = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("options", out var o) ? o.EnumerateArray().Select(OptionLabel).ToList() : new List<string>();
                    var multiple = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("multiple", out var m) && m.ValueKind == JsonValueKind.True;
                    var selected = new List<string>();
                    if (answer.ValueKind == JsonValueKind.String) selected.Add(answer.GetString()!);
                    else if (answer.ValueKind == JsonValueKind.Array) selected.AddRange(answer.EnumerateArray().Select(x => x.GetString() ?? string.Empty));
                    else if (answer.ValueKind == JsonValueKind.Number && answer.TryGetInt32(out var idx) && idx >= 0 && idx < options.Count) selected.Add(options[idx]);
                    if (selected.Count == 0 && attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("default", out var defIdx) && defIdx.TryGetInt32(out var di) && di >= 0 && di < options.Count)
                        selected.Add(options[di]);
                    var invalid = selected.Where(s => !options.Contains(s)).ToList();
                    if (invalid.Count > 0) { errors.Add(new { field = id, code = "invalid", message = $"'{label}': giá trị {string.Join(", ", invalid)} không nằm trong options." }); break; }
                    if (!multiple && selected.Count > 1) { errors.Add(new { field = id, code = "invalid", message = $"'{label}' chỉ chọn một giá trị." }); break; }
                    if (required && selected.Count == 0) { errors.Add(new { field = id, code = "missing", message = $"'{label}' là bắt buộc." }); break; }
                    body.Append("### ").Append(label).Append("\n\n").Append(selected.Count == 0 ? "_No response_" : string.Join(", ", selected)).Append("\n\n");
                    break;
                }
                case "checkboxes":
                {
                    var options = attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("options", out var o) ? o.EnumerateArray().ToList() : new List<JsonElement>();
                    var checkedSet = new HashSet<string>();
                    if (answer.ValueKind == JsonValueKind.Array) foreach (var x in answer.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) checkedSet.Add(x.GetString()!);
                    body.Append("### ").Append(label).Append("\n\n");
                    foreach (var opt in options)
                    {
                        var optLabel = OptionLabel(opt);
                        var optRequired = opt.ValueKind == JsonValueKind.Object
                            && opt.TryGetProperty("required", out var orq) && orq.ValueKind == JsonValueKind.True;
                        var isChecked = checkedSet.Contains(optLabel);
                        if (optRequired && !isChecked) errors.Add(new { field = id, code = "missing", message = $"Phải xác nhận '{optLabel}'." });
                        body.Append(isChecked ? "- [x] " : "- [ ] ").Append(optLabel).Append('\n');
                    }
                    body.Append('\n');
                    break;
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new AppException(StatusCodes.Status422UnprocessableEntity, "Form chưa hợp lệ",
                "Thiếu hoặc sai trường bắt buộc của template.", new Dictionary<string, object?> { ["errors"] = errors });
        }

        var title = request.Title.Trim();
        if (!string.IsNullOrEmpty(template.TitlePrefix) && !title.StartsWith(template.TitlePrefix.TrimEnd(), StringComparison.Ordinal))
        {
            title = template.TitlePrefix + title;
        }
        if (title.Length > 256) title = title[..256];

        using var defaults = JsonDocument.Parse(template.Defaults);
        var root = defaults.RootElement;
        var d = new TemplateDefaults(
            root.TryGetProperty("labels", out var dl) ? dl.EnumerateArray().Select(x => x.GetString()!).ToList() : new(),
            root.TryGetProperty("assignees", out var da) ? da.EnumerateArray().Select(x => x.GetString()!).ToList() : new(),
            root.TryGetProperty("type", out var dt) && dt.ValueKind == JsonValueKind.String ? dt.GetString() : null,
            root.TryGetProperty("projects", out var dp) ? dp.EnumerateArray().Select(x => x.GetString()!).ToList() : new());

        var rendered = body.ToString().TrimEnd();
        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            rendered = rendered.Length == 0 ? request.Body : rendered + "\n\n" + request.Body;
        }
        return (title, rendered, d);
    }

    /// <summary>
    /// Nhãn của một lựa chọn, chấp nhận cả hai dạng mà <see cref="ValidateSchema"/> cho qua:
    /// chuỗi trần ("P1") hoặc object có label ({ "label": "P1", "required": true }).
    /// </summary>
    /// <remarks>
    /// Trước đây mỗi nhánh chỉ chịu được một dạng, và là hai dạng ngược nhau: dropdown gọi thẳng
    /// <c>GetString()</c> nên vỡ với object, checkboxes gọi thẳng <c>TryGetProperty()</c> nên vỡ
    /// với chuỗi. Cả hai đều ném InvalidOperationException, tức 500 — mà <see cref="ValidateSchema"/>
    /// lại nhận cả hai dạng lúc lưu template, nên lỗi chỉ nổ ra khi có người đi điền biểu mẫu.
    /// </remarks>
    private static string OptionLabel(JsonElement option)
        => option.ValueKind == JsonValueKind.Object
            ? (option.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty)
            : (option.ValueKind == JsonValueKind.String ? option.GetString() ?? string.Empty : option.ToString());

    /// <summary>Mục 5.5 — kiểm tra schema: type hợp lệ, id duy nhất (trừ markdown), dropdown/checkboxes có options.</summary>
    public static void ValidateSchema(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Array) throw AppException.BadRequest("body phải là mảng element.");
        var ids = new HashSet<string>();
        foreach (var el in body.EnumerateArray())
        {
            if (!el.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || !ElementTypes.Contains(t.GetString()!))
                throw AppException.BadRequest("Mỗi element cần type ∈ markdown/input/textarea/dropdown/checkboxes.");
            var type = t.GetString()!;
            if (type != "markdown")
            {
                if (!el.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
                    throw AppException.BadRequest($"Element '{type}' cần id.");
                if (!ids.Add(id.GetString()!)) throw AppException.BadRequest($"id '{id.GetString()}' bị trùng.");
                if (!el.TryGetProperty("attributes", out var a) || !a.TryGetProperty("label", out _))
                    throw AppException.BadRequest($"Element '{id.GetString()}' cần attributes.label.");
                if (type is "dropdown" or "checkboxes")
                {
                    if (!a.TryGetProperty("options", out var o) || o.ValueKind != JsonValueKind.Array || o.GetArrayLength() == 0)
                        throw AppException.BadRequest($"Element '{id.GetString()}' cần options không rỗng.");
                }
            }
            else if (!el.TryGetProperty("attributes", out var ma) || !ma.TryGetProperty("value", out _))
            {
                throw AppException.BadRequest("Element markdown cần attributes.value.");
            }
        }
    }

    private static string DefaultsJson(TemplateRequest request)
        => JsonSerializer.Serialize(new
        {
            labels = request.Labels ?? new List<string>(),
            assignees = request.Assignees ?? new List<string>(),
            type = request.Type,
            projects = request.Projects ?? new List<string>()
        });

    private async Task<(Project, TicketTemplate)> FindAsync(string slug, Guid id, CancellationToken ct)
    {
        var project = await _q.ProjectAsync(slug, ct);
        var template = await _db.TicketTemplates.FirstOrDefaultAsync(t => t.Id == id && t.ProjectId == project.Id, ct)
                       ?? throw AppException.NotFound("Không tìm thấy template.");
        return (project, template);
    }

    private static TemplateResponse ToResponse(TicketTemplate t, string slug)
        => new(t.Id, slug, t.Name, t.Description, t.TitlePrefix,
            JsonDocument.Parse(t.Defaults).RootElement.Clone(), JsonDocument.Parse(t.BodySchema).RootElement.Clone(), t.IsEnabled, t.Position);
}

[ApiController]
[Route("api/projects/{project}/templates")]
[Produces("application/json")]
public sealed class TemplatesController : ControllerBase
{
    private readonly TemplateService _templates;
    public TemplatesController(TemplateService templates) => _templates = templates;

    [HttpGet]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<IReadOnlyList<TemplateResponse>>> List(string project, [FromQuery] bool includeDisabled, CancellationToken ct)
        => Ok(await _templates.ListAsync(project, includeDisabled, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.TicketRead)]
    [EnableRateLimiting(RateLimitPolicies.TicketRead)]
    public async Task<ActionResult<TemplateResponse>> Get(string project, Guid id, CancellationToken ct)
        => Ok(await _templates.GetAsync(project, id, ct));

    [HttpPost]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<TemplateResponse>> Create(string project, TemplateRequest request, CancellationToken ct)
    {
        var created = await _templates.CreateAsync(project, request, ct);
        return CreatedAtAction(nameof(Get), new { project, id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<ActionResult<TemplateResponse>> Update(string project, Guid id, TemplateRequest request, CancellationToken ct)
        => Ok(await _templates.UpdateAsync(project, id, request, ct));

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.TicketWrite)]
    [EnableRateLimiting(RateLimitPolicies.TicketWrite)]
    public async Task<IActionResult> Delete(string project, Guid id, CancellationToken ct)
    {
        await _templates.DeleteAsync(project, id, ct);
        return NoContent();
    }
}
