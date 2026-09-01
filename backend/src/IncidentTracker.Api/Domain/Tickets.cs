using System.Text.Json.Serialization;
using IncidentTracker.Api.Common;

namespace IncidentTracker.Api.Domain;

// ===========================================================================
// Module Tickets — Architecture.md v3.1, mục 5.1–5.4. Tái hiện GitHub Issues.
// Nhóm A (tái hiện GitHub) và nhóm B (mở rộng riêng: INTERNAL_NOTE, priority/SLA)
// được ghi chú trên từng thành phần.
// ===========================================================================

// ---------------------------------------------------------------------------
// Enum — lưu text UPPER_SNAKE trong DB và JSON (EnumNaming).
// ---------------------------------------------------------------------------

[JsonConverter(typeof(UpperSnakeJsonConverter<TicketState>))]
public enum TicketState { Open, Closed }

/// <summary>GitHub <c>state_reason</c>: completed / not_planned / duplicate / reopened.</summary>
[JsonConverter(typeof(UpperSnakeJsonConverter<StateReason>))]
public enum StateReason { Completed, NotPlanned, Duplicate, Reopened }

/// <summary>GitHub <c>active_lock_reason</c>.</summary>
[JsonConverter(typeof(UpperSnakeJsonConverter<LockReason>))]
public enum LockReason { OffTopic, TooHeated, Resolved, Spam }

/// <summary>Bản vẽ 5.3: PUBLIC / INTERNAL (chỉ nhân viên) / ACTOR_ONLY (MENTIONED, SUBSCRIBED).</summary>
[JsonConverter(typeof(UpperSnakeJsonConverter<EventVisibility>))]
public enum EventVisibility { Public, Internal, ActorOnly }

/// <summary>8 loại reaction của GitHub.</summary>
[JsonConverter(typeof(UpperSnakeJsonConverter<ReactionType>))]
public enum ReactionType { ThumbsUp, ThumbsDown, Laugh, Hooray, Confused, Heart, Rocket, Eyes }

[JsonConverter(typeof(UpperSnakeJsonConverter<SubscriptionState>))]
public enum SubscriptionState { Subscribed, Unsubscribed, Ignored }

/// <summary>Lý do auto-subscribe (mục 2.8).</summary>
[JsonConverter(typeof(UpperSnakeJsonConverter<SubscriptionReason>))]
public enum SubscriptionReason { Author, Assigned, Commented, Mentioned, Manual, StateChange, TeamMention }

/// <summary>Mức watch project (≈ watch repository).</summary>
[JsonConverter(typeof(UpperSnakeJsonConverter<WatchLevel>))]
public enum WatchLevel { All, Participating, Ignore, Custom }

/// <summary>Quan hệ giữa ticket. BLOCKING là chiều đảo của BLOCKED_BY, không lưu hai lần.</summary>
[JsonConverter(typeof(UpperSnakeJsonConverter<RelationType>))]
public enum RelationType { BlockedBy, CrossReference, DuplicateOf }

/// <summary>Mở rộng riêng (nhóm B) — mức ưu tiên phục vụ SLA.</summary>
[JsonConverter(typeof(UpperSnakeJsonConverter<TicketPriority>))]
public enum TicketPriority { P0, P1, P2, P3 }

[JsonConverter(typeof(UpperSnakeJsonConverter<BoardVisibility>))]
public enum BoardVisibility { Private, Internal }

/// <summary>Lý do ẩn (minimize) comment — GitHub: abuse/off-topic/outdated/resolved/spam/duplicate.</summary>
[JsonConverter(typeof(UpperSnakeJsonConverter<HideReason>))]
public enum HideReason { Abuse, OffTopic, Outdated, Resolved, Spam, Duplicate }

/// <summary>Lý do của một thread thông báo — tên theo GitHub (mục 5.1 NOTIFICATION_THREADS).</summary>
[JsonConverter(typeof(UpperSnakeJsonConverter<NotificationReason>))]
public enum NotificationReason { Assign, Author, Comment, Manual, Mention, StateChange, Subscribed, TeamMention, SlaBreach }

// ---------------------------------------------------------------------------
// Entity
// ---------------------------------------------------------------------------

/// <summary>
/// ≈ repository của GitHub: phạm vi của <c>number</c>, label, milestone, template, webhook.
/// Hai cờ <c>StrictClosePolicy</c> và <c>AutoReopenOnCustomerComment</c> là mở rộng, mặc định
/// <c>false</c> = đúng hành vi GitHub (Product Owner đã duyệt 06/09/2026).
/// </summary>
public class Project
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public bool BlankIssuesEnabled { get; set; } = true;
    public bool StrictClosePolicy { get; set; }
    public bool AutoReopenOnCustomerComment { get; set; }

    /// <summary>
    /// Mở rộng riêng: khách hàng (Read) chỉ thấy ticket do mình tạo/được assign. Mặc định
    /// <c>false</c> = giống GitHub (mọi người đọc được mọi issue trong repo).
    /// </summary>
    public bool CustomersSeeOnlyOwn { get; set; }

    /// <summary>Bộ đếm cấp <c>#N</c>; tăng bằng <c>UPDATE ... RETURNING</c> trong transaction tạo ticket.</summary>
    public int NextTicketNumber { get; set; } = 1;

    /// <summary>≈ <c>config.yml</c>: <c>[{name, url, about}]</c> (jsonb).</summary>
    public string ContactLinks { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
    public bool IsArchived { get; set; }

    public List<Ticket> Tickets { get; set; } = new();
    public List<Label> Labels { get; set; } = new();
    public List<Milestone> Milestones { get; set; } = new();
}

/// <summary>Issue Type cấp tổ chức (toàn hệ thống): Bug/Feature/Task + tuỳ biến.</summary>
public class IssueType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Color { get; set; } = "gray";
    public string? Description { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Position { get; set; }
}

/// <summary>
/// Snapshot / projection đồng bộ của một issue (mục 5.2). Mọi cột đều suy ra được bằng replay
/// <see cref="TicketEvent"/>; cập nhật trong cùng transaction với event.
/// </summary>
public class Ticket
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    /// <summary>Unique <c>(project_id, number)</c>; không tái sử dụng.</summary>
    public int Number { get; set; }

    public string Title { get; set; } = null!;

    /// <summary>Markdown thô (GFM). KHÔNG sanitize khi lưu (BR-SEC-02).</summary>
    public string Body { get; set; } = string.Empty;

    public Guid AuthorId { get; set; }
    public User Author { get; set; } = null!;

    public TicketState State { get; set; } = TicketState.Open;
    public StateReason? StateReason { get; set; }
    public Guid? DuplicateOfTicketId { get; set; }
    public Guid? ClosedById { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsLocked { get; set; }
    public LockReason? ActiveLockReason { get; set; }
    public bool IsPinned { get; set; }

    public Guid? TypeId { get; set; }
    public IssueType? Type { get; set; }

    public Guid? ParentTicketId { get; set; }
    public Ticket? ParentTicket { get; set; }
    public int? SubIssuePosition { get; set; }

    public Guid? MilestoneId { get; set; }
    public Milestone? Milestone { get; set; }

    /// <summary>Mở rộng riêng (nhóm B).</summary>
    public TicketPriority? Priority { get; set; }
    public DateTimeOffset? SlaDueAt { get; set; }
    public DateTimeOffset? FirstResponseAt { get; set; }

    /// <summary>Projection: số <c>COMMENTED</c> chưa bị xoá.</summary>
    public int CommentsCount { get; set; }

    /// <summary>Projection jsonb: <c>{"total": n, "THUMBS_UP": n, ...}</c>.</summary>
    public string ReactionsSummary { get; set; } = "{}";

    /// <summary>= <c>ticket_version</c> của event cuối; dùng cho ETag / If-Match (BR-CONC-01).</summary>
    public int Version { get; set; }

    public bool IsDeleted { get; set; }

    /// <summary>UC-14: cột generated <c>to_tsvector('simple', title || body)</c> + GIN (read model FTS).</summary>
    public NpgsqlTypes.NpgsqlTsVector? SearchVector { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<TicketLabel> Labels { get; set; } = new();
    public List<TicketAssignee> Assignees { get; set; } = new();
    public List<Ticket> SubIssues { get; set; } = new();
}

/// <summary>
/// Event Store — nguồn sự thật (mục 5.3). Bảng partition theo tháng trên <c>created_at</c>;
/// PK vật lý <c>(sequence, created_at)</c>. Không UPDATE/DELETE (BR-EV-01).
/// </summary>
public class TicketEvent
{
    /// <summary>Thứ tự toàn cục — cursor keyset duy nhất (BR-SCALE-01).</summary>
    public long Sequence { get; set; }

    /// <summary>Public id: permalink, reaction/hide trỏ vào comment.</summary>
    public Guid Id { get; set; }

    public Guid TicketId { get; set; }

    /// <summary>Vị trí trong stream của ticket; unique <c>(ticket_id, ticket_version)</c> → optimistic concurrency.</summary>
    public int TicketVersion { get; set; }

    /// <summary><c>null</c> = hệ thống (SLA job, closing keyword, projector).</summary>
    public Guid? ActorId { get; set; }

    /// <summary>Discriminator — xem <see cref="TicketEventTypes"/>.</summary>
    public string EventType { get; set; } = null!;

    /// <summary>jsonb, schema theo <see cref="EventType"/>.</summary>
    public string Payload { get; set; } = "{}";

    public EventVisibility Visibility { get; set; } = EventVisibility.Public;
    public Guid? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Danh mục <c>event_type</c> (mục 5.4) — hằng chuỗi để payload/handler tham chiếu thống nhất.</summary>
public static class TicketEventTypes
{
    /// <summary>
    /// Event <b>không hiện thành dòng</b> trên timeline.
    ///
    /// Chúng vẫn được ghi vào sổ sự kiện — đó là bản ghi kiểm toán — nhưng không phải thứ đáng
    /// chiếm một dòng trong dòng thời gian người dùng đọc: thả emoji, theo dõi, sửa bình luận…
    ///
    /// <b>Phải là cùng một danh sách</b> cho cả hai nơi quyết định "cái gì thành dòng": câu truy
    /// vấn timeline (<c>TimelineService</c>) và bộ phát real-time
    /// (<c>TicketEventBroadcaster</c>). Trước đây mỗi bên tự giữ một luật — truy vấn lọc, kênh
    /// real-time phát tất — nên thả một emoji là màn hình hiện dòng "… reacted" rồi biến mất ngay
    /// ở lần tải kế tiếp. Hai nguồn sự thật cho một câu hỏi thì sớm muộn cũng lệch.
    /// </summary>
    public static readonly string[] Silent =
    {
        Reacted, Unreacted, Subscribed, Unsubscribed,
        BodyEdited, CommentEdited, CommentDeleted,
        CommentHidden, CommentUnhidden, EditHistoryDeleted,
        FirstResponse, Opened
    };


    // Nội dung & chỉnh sửa
    public const string Commented = "COMMENTED";
    public const string CommentEdited = "COMMENT_EDITED";
    public const string CommentDeleted = "COMMENT_DELETED";
    public const string CommentHidden = "COMMENT_HIDDEN";
    public const string CommentUnhidden = "COMMENT_UNHIDDEN";
    public const string EditHistoryDeleted = "EDIT_HISTORY_DELETED";
    public const string Renamed = "RENAMED";
    public const string BodyEdited = "BODY_EDITED";
    public const string InternalNote = "INTERNAL_NOTE";

    // Vòng đời
    public const string Opened = "OPENED";
    public const string Closed = "CLOSED";
    public const string Reopened = "REOPENED";
    public const string Locked = "LOCKED";
    public const string Unlocked = "UNLOCKED";
    public const string Pinned = "PINNED";
    public const string Unpinned = "UNPINNED";
    public const string Transferred = "TRANSFERRED";
    public const string Deleted = "DELETED";
    public const string ConvertedToDiscussion = "CONVERTED_TO_DISCUSSION";

    // Tổ chức
    public const string Labeled = "LABELED";
    public const string Unlabeled = "UNLABELED";
    public const string Milestoned = "MILESTONED";
    public const string Demilestoned = "DEMILESTONED";
    public const string Assigned = "ASSIGNED";
    public const string Unassigned = "UNASSIGNED";
    public const string Typed = "TYPED";
    public const string Untyped = "UNTYPED";
    public const string AddedToBoard = "ADDED_TO_BOARD";
    public const string RemovedFromBoard = "REMOVED_FROM_BOARD";
    public const string BoardColumnChanged = "BOARD_COLUMN_CHANGED";
    public const string PriorityChanged = "PRIORITY_CHANGED";

    // Quan hệ
    public const string SubIssueAdded = "SUB_ISSUE_ADDED";
    public const string SubIssueRemoved = "SUB_ISSUE_REMOVED";
    public const string ParentIssueAdded = "PARENT_ISSUE_ADDED";
    public const string ParentIssueRemoved = "PARENT_ISSUE_REMOVED";
    public const string BlockedByAdded = "BLOCKED_BY_ADDED";
    public const string BlockedByRemoved = "BLOCKED_BY_REMOVED";
    public const string BlockingAdded = "BLOCKING_ADDED";
    public const string BlockingRemoved = "BLOCKING_REMOVED";
    public const string MarkedAsDuplicate = "MARKED_AS_DUPLICATE";
    public const string UnmarkedAsDuplicate = "UNMARKED_AS_DUPLICATE";
    public const string CrossReferenced = "CROSS_REFERENCED";
    public const string Mentioned = "MENTIONED";
    public const string Connected = "CONNECTED";
    public const string Disconnected = "DISCONNECTED";
    public const string Referenced = "REFERENCED";

    // Tương tác
    public const string Reacted = "REACTED";
    public const string Unreacted = "UNREACTED";
    public const string Subscribed = "SUBSCRIBED";
    public const string Unsubscribed = "UNSUBSCRIBED";

    // Mở rộng riêng
    public const string SlaWarning = "SLA_WARNING";
    public const string SlaBreached = "SLA_BREACHED";
    public const string Escalated = "ESCALATED";
    public const string FirstResponse = "FIRST_RESPONSE";

    /// <summary>Event có thân Markdown (được render + sanitize khi trả ra).</summary>
    public static bool HasBody(string type)
        => type is Commented or CommentEdited or InternalNote or BodyEdited;

    /// <summary>
    /// Event KHÔNG hiện thành một dòng trên timeline (GitHub cũng ẩn): reaction, subscribe, body edit.
    ///
    /// Đọc từ <see cref="Silent"/> chứ không liệt kê lại: liệt kê hai lần thì thêm một loại event
    /// im lặng mà quên một chỗ là hai nơi trả lời khác nhau cho cùng một câu hỏi.
    /// </summary>
    public static bool IsSilent(string type) => Array.IndexOf(Silent, type) >= 0;
}

public class TicketReference
{
    public Guid SourceId { get; set; }
    public Guid TargetId { get; set; }
    public RelationType RelationType { get; set; }
    public Guid? CreatedByEventId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class Label
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Name { get; set; } = null!;
    /// <summary>6 ký tự hex, không dấu #.</summary>
    public string ColorHex { get; set; } = "ededed";
    public string? Description { get; set; }
    public bool IsDefault { get; set; }
    public bool IsArchived { get; set; }
}

public class TicketLabel
{
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public Guid LabelId { get; set; }
    public Label Label { get; set; } = null!;
}

public class Milestone
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public int Number { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public DateOnly? DueOn { get; set; }
    public TicketState State { get; set; } = TicketState.Open;
    public DateTimeOffset? ClosedAt { get; set; }
    public int OpenCount { get; set; }
    public int ClosedCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class TicketAssignee
{
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset AssignedAt { get; set; }
}

/// <summary>Reaction trên body ticket (<c>EventId = null</c>) hoặc trên một comment.</summary>
public class TicketReaction
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public Guid? EventId { get; set; }
    public Guid UserId { get; set; }
    public ReactionType ReactionType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class TicketSubscription
{
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public SubscriptionState State { get; set; }
    public SubscriptionReason Reason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ProjectWatch
{
    public Guid ProjectId { get; set; }
    public Guid UserId { get; set; }
    public WatchLevel Level { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>1 dòng / (user, ticket) — GitHub gom thông báo theo thread (R15).</summary>
public class NotificationThread
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TicketId { get; set; }
    public Ticket Ticket { get; set; } = null!;
    public NotificationReason Reason { get; set; }
    public bool Unread { get; set; } = true;
    public bool IsDone { get; set; }
    public bool IsSaved { get; set; }
    public Guid? LastEventId { get; set; }
    public string? LastEventType { get; set; }
    public Guid? LastActorId { get; set; }
    public DateTimeOffset? LastReadAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Issue Form (mục 5.5) — <c>Defaults</c> và <c>BodySchema</c> là jsonb.</summary>
public class TicketTemplate
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string? TitlePrefix { get; set; }
    public string Defaults { get; set; } = "{}";
    public string BodySchema { get; set; } = "[]";
    public bool IsEnabled { get; set; } = true;
    public int Position { get; set; }
}

/// <summary>≈ GitHub Projects (v2) rút gọn: cột = option của trường Status.</summary>
public class Board
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }

    /// <summary>
    /// Project của board. <c>null</c> = **board chung**, cố ý xuyên project.
    ///
    /// Board sinh ra để gom việc từ nhiều project vào một đợt làm, và dữ liệu thật đang dùng đúng
    /// như vậy. Ép mỗi board vào một project sẽ bỏ mất công dụng đó, nên board chung vẫn tồn tại
    /// và tự lọc thẻ theo quyền của người xem.
    /// </summary>
    public Guid? ProjectId { get; set; }

    public BoardVisibility Visibility { get; set; } = BoardVisibility.Internal;
    /// <summary>jsonb: <c>{"auto_add_query": "...", "item_closed_to_column_id": "..."}</c>.</summary>
    public string Automation { get; set; } = "{}";
    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsClosed { get; set; }

    public List<BoardColumn> Columns { get; set; } = new();
    public List<BoardItem> Items { get; set; } = new();
}

public class BoardColumn
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public Board Board { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Color { get; set; }
    public int Position { get; set; }
}

public class BoardItem
{
    public Guid Id { get; set; }
    public Guid BoardId { get; set; }
    public Board Board { get; set; } = null!;
    /// <summary><c>null</c> khi là draft item.</summary>
    public Guid? TicketId { get; set; }
    public Ticket? Ticket { get; set; }
    public string? DraftTitle { get; set; }
    public Guid? ColumnId { get; set; }
    public BoardColumn? Column { get; set; }
    public int Position { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// UC-14 — read model text comment (≈ phần <c>comments</c> của <c>Ticket_Summary</c>): Projector gộp thân
/// comment hiện hành (public / internal tách riêng để Customer không tìm được INTERNAL) + login người
/// comment / được mention. Rebuild được từ Event Store.
/// </summary>
public class TicketSearchComment
{
    public Guid TicketId { get; set; }
    public string PublicText { get; set; } = string.Empty;
    public string InternalText { get; set; } = string.Empty;
    /// <summary>Login người đã comment (public), cách nhau bằng khoảng trắng.</summary>
    public string Commenters { get; set; } = string.Empty;
    /// <summary>Login người được mention.</summary>
    public string Mentioned { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>BR-REL-05: URL cũ của ticket đã transfer trả 301 tới URL mới.</summary>
public class TicketRedirect
{
    public Guid ProjectId { get; set; }
    public int Number { get; set; }
    public Guid TicketId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>BR-REL-01 — tách khỏi bảng partition; PK <c>(key, user_id)</c>; TTL 24h.</summary>
public class IdempotencyKey
{
    public string Key { get; set; } = null!;
    public Guid UserId { get; set; }
    public string RequestHash { get; set; } = null!;
    public int ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public string? ResponseContentType { get; set; }
    public string? ResponseHeaders { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Mở rộng riêng (nhóm B) — UC-16.</summary>
public class SlaPolicy
{
    public Guid Id { get; set; }
    public TicketPriority Priority { get; set; }
    public int ResponseTimeMinutes { get; set; }
    public int ResolutionTimeMinutes { get; set; }
    /// <summary>Quá hạn quá số phút này thì ESCALATED lên Lead.</summary>
    public int EscalateAfterMinutes { get; set; } = 60;
    public bool IsActive { get; set; } = true;
}

public class WebhookSubscription
{
    public Guid Id { get; set; }
    /// <summary><c>null</c> = toàn hệ thống.</summary>
    public Guid? ProjectId { get; set; }
    public string TargetUrl { get; set; } = null!;
    public string SecretHmac { get; set; } = null!;
    /// <summary>jsonb mảng tên event: <c>["issues","issue_comment",...]</c>.</summary>
    public string Events { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
    public int ConsecutiveFailures { get; set; }
    public Guid CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class WebhookDelivery
{
    /// <summary>= header <c>X-Delivery-Id</c>.</summary>
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public WebhookSubscription Subscription { get; set; } = null!;
    public Guid? EventId { get; set; }
    public string EventName { get; set; } = null!;
    public string Action { get; set; } = null!;
    /// <summary>Body đã gửi — giữ để Redeliver gửi lại đúng payload.</summary>
    public string RequestBody { get; set; } = null!;
    public string RequestHeaders { get; set; } = "{}";
    public int? HttpStatus { get; set; }
    public int? DurationMs { get; set; }
    public string? ResponseBody { get; set; }
    public string? Error { get; set; }
    public bool IsRedelivery { get; set; }
    public int Attempt { get; set; } = 1;
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
