using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;

namespace IncidentTracker.Api.Modules.Tickets.Organization;

// ---------------- Projects ----------------

public sealed record ProjectResponse(Guid Id, string Slug, string Name, string? Description, bool BlankIssuesEnabled,
    bool StrictClosePolicy, bool AutoReopenOnCustomerComment, bool CustomersSeeOnlyOwn, JsonElement ContactLinks,
    bool IsArchived, int OpenTickets, int ClosedTickets, DateTimeOffset CreatedAt);

public sealed class CreateProjectRequest
{
    [Required, RegularExpression("^[a-z0-9][a-z0-9._-]{0,98}$"), MaxLength(100)]
    public string Slug { get; set; } = string.Empty;

    [Required, MinLength(1), MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    /// <summary>
    /// Tạo sẵn ở trạng thái lưu trữ. Ít dùng, nhưng có chỗ dùng thật: dựng lại một dự án cũ để
    /// giữ chỗ mà chưa muốn nó hiện trong danh sách của ai.
    ///
    /// Có trường này thì màn hình tạo và màn hình sửa bày ra cùng một tập lựa chọn, và không phải
    /// gọi POST xong PATCH thêm lần nữa — hai lời gọi thì lời thứ hai hỏng là để lại dự án nửa vời.
    /// </summary>
    public bool IsArchived { get; set; }
}

public sealed class UpdateProjectRequest
{
    [MaxLength(200)] public string? Name { get; set; }
    [MaxLength(2000)] public string? Description { get; set; }
    public bool? BlankIssuesEnabled { get; set; }
    public bool? StrictClosePolicy { get; set; }
    public bool? AutoReopenOnCustomerComment { get; set; }
    public bool? CustomersSeeOnlyOwn { get; set; }
    public bool? IsArchived { get; set; }
    /// <summary><c>[{name, url, about}]</c></summary>
    public JsonElement? ContactLinks { get; set; }
}

// ---------------- Labels ----------------

public sealed class LabelRequest
{
    [Required, MinLength(1), MaxLength(50)]
    public string Name { get; set; } = string.Empty;

    [RegularExpression("^#?[0-9a-fA-F]{6}$")]
    public string? Color { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }
}

public sealed class UpdateLabelRequest
{
    [MaxLength(50)] public string? NewName { get; set; }
    [RegularExpression("^#?[0-9a-fA-F]{6}$")] public string? Color { get; set; }
    [MaxLength(200)] public string? Description { get; set; }
}

public sealed class TicketLabelsRequest
{
    [Required]
    public List<string> Labels { get; set; } = new();
}

public sealed class TicketAssigneesRequest
{
    [Required]
    public List<string> Assignees { get; set; } = new();
}

// ---------------- Milestones ----------------

public sealed class MilestoneRequest
{
    [Required, MinLength(1), MaxLength(200)]
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly? DueOn { get; set; }
}

public sealed class UpdateMilestoneRequest
{
    [MaxLength(200)] public string? Title { get; set; }
    public string? Description { get; set; }
    public DateOnly? DueOn { get; set; }
    public bool? ClearDueOn { get; set; }
    public TicketState? State { get; set; }
}

// ---------------- Issue types ----------------

public sealed class IssueTypeRequest
{
    [Required, MinLength(1), MaxLength(50)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(20)] public string? Color { get; set; }
    [MaxLength(500)] public string? Description { get; set; }
    public bool? IsEnabled { get; set; }
}

// ---------------- Templates (mục 5.5) ----------------

public sealed record TemplateResponse(Guid Id, string ProjectSlug, string Name, string? Description, string? TitlePrefix,
    JsonElement Defaults, JsonElement Body, bool IsEnabled, int Position);

public sealed class TemplateRequest
{
    [Required, MinLength(1), MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(500)] public string? Description { get; set; }
    /// <summary>≈ khoá <c>title</c> trong issue form: tiền tố tiêu đề, vd. <c>[Bug]: </c>.</summary>
    [MaxLength(100)] public string? Title { get; set; }
    public List<string>? Labels { get; set; }
    public List<string>? Assignees { get; set; }
    public string? Type { get; set; }
    public List<string>? Projects { get; set; }
    /// <summary>Mảng element: markdown / input / textarea / dropdown / checkboxes.</summary>
    [Required]
    public JsonElement Body { get; set; }
    public bool? IsEnabled { get; set; }
    public int? Position { get; set; }
}

// ---------------- Boards ----------------

public sealed record BoardColumnResponse(Guid Id, string Name, string? Color, int Position, int ItemCount);

public sealed record BoardItemResponse(Guid Id, Guid? ColumnId, int Position, string? DraftTitle, TicketRefResponse? Ticket,
    IReadOnlyList<LabelResponse> Labels, IReadOnlyList<UserSummary> Assignees, DateTimeOffset AddedAt);

public sealed record BoardResponse(Guid Id, string Name, string? Description, BoardVisibility Visibility, JsonElement Automation,
    bool IsClosed, UserSummary? CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<BoardColumnResponse> Columns, int ItemCount);

public sealed record BoardDetailResponse(BoardResponse Board, IReadOnlyList<BoardItemResponse> Items);

public sealed class BoardRequest
{
    [Required, MinLength(1), MaxLength(200)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(2000)] public string? Description { get; set; }
    public BoardVisibility? Visibility { get; set; }
    /// <summary><c>{"auto_add_query": "...", "item_closed_to_column_id": "..."}</c></summary>
    public JsonElement? Automation { get; set; }
    public bool? IsClosed { get; set; }
}

public sealed class BoardColumnRequest
{
    [Required, MinLength(1), MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(20)] public string? Color { get; set; }
    public int? Position { get; set; }
}

public sealed class BoardItemRequest
{
    /// <summary><c>project#N</c> hoặc uuid ticket.</summary>
    public string? Ticket { get; set; }
    [MaxLength(256)] public string? DraftTitle { get; set; }
    public Guid? ColumnId { get; set; }
}

public sealed class MoveBoardItemRequest
{
    public Guid? ColumnId { get; set; }
    /// <summary>Vị trí mới trong cột (0-based). Null = cuối cột.</summary>
    public int? Position { get; set; }
}
