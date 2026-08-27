using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;

namespace IncidentTracker.Api.Modules.Tickets;

// ---------------------------------------------------------------------------
// Response
// ---------------------------------------------------------------------------

public sealed record LabelResponse(Guid Id, string Name, string ColorHex, string? Description, bool IsDefault, bool IsArchived);

public sealed record MilestoneResponse(Guid Id, int Number, string Title, string? Description, DateOnly? DueOn,
    TicketState State, DateTimeOffset? ClosedAt, int OpenCount, int ClosedCount, DateTimeOffset CreatedAt);

public sealed record IssueTypeResponse(Guid Id, string Name, string Color, string? Description, bool IsEnabled);

public sealed record TicketRefResponse(Guid Id, string ProjectSlug, int Number, string Title, TicketState State, StateReason? StateReason);

public sealed record SubIssuesSummary(int Total, int Completed, int PercentCompleted);

/// <summary>Snapshot ticket — khớp Issue object của GitHub REST (mục 5.2).</summary>
public sealed record TicketResponse(
    Guid Id,
    string ProjectSlug,
    int Number,
    string Title,
    string Body,
    string BodyHtml,
    UserSummary Author,
    string AuthorAssociation,
    TicketState State,
    StateReason? StateReason,
    TicketRefResponse? DuplicateOf,
    UserSummary? ClosedBy,
    DateTimeOffset? ClosedAt,
    bool Locked,
    LockReason? ActiveLockReason,
    bool Pinned,
    IssueTypeResponse? Type,
    TicketRefResponse? Parent,
    MilestoneResponse? Milestone,
    IReadOnlyList<LabelResponse> Labels,
    IReadOnlyList<UserSummary> Assignees,
    UserSummary? Assignee,
    TicketPriority? Priority,
    DateTimeOffset? SlaDueAt,
    DateTimeOffset? FirstResponseAt,
    int Comments,
    IReadOnlyDictionary<string, int> Reactions,

    /// <summary>
    /// Những reaction do <b>chính người đang xem</b> thả trên thân ticket.
    ///
    /// Không suy ra được từ <see cref="Reactions"/>: đó là bảng đếm đã gộp, nó nói "có 9 người
    /// thích" chứ không nói bạn có nằm trong 9 người đó không. Thiếu trường này thì nút reaction
    /// chỉ sáng lên từ lúc bấm cho tới lần tải trang kế tiếp.
    /// </summary>
    IReadOnlyList<ReactionType> ViewerReactions,

    SubIssuesSummary SubIssuesSummary,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Warnings);

public sealed record CommentResponse(
    Guid Id,
    long Sequence,
    Guid TicketId,
    UserSummary? Author,
    string AuthorAssociation,
    string Body,
    string BodyHtml,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool IsHidden,
    HideReason? HiddenReason,
    IReadOnlyDictionary<string, int> Reactions,
    IReadOnlyList<ReactionType> ViewerReactions);

public sealed record CommentEditResponse(Guid EventId, UserSummary? Editor, string Body, DateTimeOffset EditedAt, bool IsCurrent);

/// <summary>Trang keyset: <c>next_cursor</c> = null khi hết.</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, int? TotalCount = null);

// ---------------------------------------------------------------------------
// Request
// ---------------------------------------------------------------------------

public sealed class CreateTicketRequest
{
    [Required, MinLength(1), MaxLength(256)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(65536)]
    public string? Body { get; set; }

    /// <summary>Tên label (tạo/gắn cần Triage; mức Read gửi lên sẽ bị bỏ qua giống GitHub).</summary>
    public List<string>? Labels { get; set; }

    /// <summary>Login của assignee.</summary>
    public List<string>? Assignees { get; set; }

    /// <summary>Số milestone trong project.</summary>
    public int? Milestone { get; set; }

    /// <summary>Tên Issue Type.</summary>
    public string? Type { get; set; }

    public TicketPriority? Priority { get; set; }

    /// <summary>UC-17: tạo từ template — body được render từ <see cref="FormAnswers"/> theo schema template.</summary>
    public Guid? TemplateId { get; set; }

    public Dictionary<string, JsonElement>? FormAnswers { get; set; }

    /// <summary>UC-10: tạo trực tiếp làm sub-issue của ticket cha (number trong cùng project).</summary>
    public int? ParentNumber { get; set; }
}

/// <summary>PATCH — mọi trường tuỳ chọn; null = không đổi (giống GitHub <c>PATCH /issues/{n}</c>).</summary>
public sealed class UpdateTicketRequest
{
    [MaxLength(256)]
    public string? Title { get; set; }

    [MaxLength(65536)]
    public string? Body { get; set; }

    public TicketState? State { get; set; }
    public StateReason? StateReason { get; set; }

    /// <summary><c>project#N</c> hoặc <c>N</c> — bắt buộc khi <c>StateReason = DUPLICATE</c>.</summary>
    public string? DuplicateOf { get; set; }

    /// <summary>Thay toàn bộ danh sách label (tên). Mảng rỗng = gỡ hết.</summary>
    public List<string>? Labels { get; set; }

    /// <summary>Thay toàn bộ danh sách assignee (login). Mảng rỗng = gỡ hết.</summary>
    public List<string>? Assignees { get; set; }

    /// <summary>Số milestone; <c>0</c> = gỡ milestone (GitHub dùng null nhưng null ở đây nghĩa là "không đổi").</summary>
    public int? Milestone { get; set; }

    /// <summary>Tên Issue Type; chuỗi rỗng = gỡ type.</summary>
    public string? Type { get; set; }

    /// <summary>Có mặt = đổi priority; gửi <c>"priority": null</c> không phân biệt được với không gửi nên dùng <see cref="ClearPriority"/> để gỡ.</summary>
    public TicketPriority? Priority { get; set; }
    public bool? ClearPriority { get; set; }
}

public sealed class CommentRequest
{
    [Required, MinLength(1), MaxLength(65536)]
    public string Body { get; set; } = string.Empty;
}

public sealed class LockRequest
{
    public LockReason? LockReason { get; set; }
}

public sealed class MinimizeRequest
{
    [Required]
    public HideReason Reason { get; set; }
}

public sealed class TransferRequest
{
    [Required, MaxLength(100)]
    public string ToProject { get; set; } = string.Empty;
}

/// <summary>Bộ lọc cơ bản của <c>GET /projects/{p}/tickets</c> (Query DSL đầy đủ ở modify_07 qua <c>q</c>).</summary>
public sealed class TicketListQuery
{
    /// <summary>open (mặc định) / closed / all</summary>
    public string? State { get; set; }
    public string? Labels { get; set; }
    public string? Milestone { get; set; }
    public string? Assignee { get; set; }
    public string? Author { get; set; }
    public string? Type { get; set; }
    public string? Q { get; set; }
    /// <summary>created (mặc định) / updated / comments</summary>
    public string? Sort { get; set; }
    /// <summary>desc (mặc định) / asc</summary>
    public string? Direction { get; set; }
    public string? Cursor { get; set; }

    [Microsoft.AspNetCore.Mvc.FromQuery(Name = "per_page")]
    public int? PerPage { get; set; }
}
