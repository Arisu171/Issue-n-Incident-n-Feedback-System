using System.Security.Claims;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Tickets.Infrastructure;

namespace IncidentTracker.Api.Modules.Tickets;

/// <summary>
/// Ma trận quyền theo hành động (Architecture v3.1, mục 10.1) — Read / Triage / Write / Admin ánh
/// xạ từ permission. Permission tĩnh chặn ở controller; lớp này trả lời câu hỏi còn lại:
/// "trên bản ghi này, người này có được làm việc đó không".
/// </summary>
public static class TicketAccess
{
    public static AccessLevel Level(ClaimsPrincipal user) => TimelineAccess.LevelOf(user);

    public static bool IsAtLeast(ClaimsPrincipal user, AccessLevel level) => Level(user) >= level;

    public static bool IsAuthor(ClaimsPrincipal user, Ticket ticket) => ticket.AuthorId == user.GetUserId();

    /// <summary>Mở rộng: khi project bật <c>customers_see_only_own</c>, mức Read chỉ thấy ticket mình tạo/được assign.</summary>
    public static bool CanRead(ClaimsPrincipal user, Ticket ticket, Project project)
    {
        if (!user.HasPermission(Permissions.TicketRead))
        {
            return false;
        }

        if (!project.CustomersSeeOnlyOwn || IsAtLeast(user, AccessLevel.Triage))
        {
            return true;
        }

        var uid = user.GetUserId();
        return ticket.AuthorId == uid || ticket.Assignees.Any(a => a.UserId == uid);
    }

    public static void EnsureCanRead(ClaimsPrincipal user, Ticket ticket, Project project)
    {
        if (!CanRead(user, ticket, project))
        {
            throw AppException.Forbidden("Bạn không có quyền xem ticket này.");
        }
    }

    /// <summary>Sửa title/body: tác giả, hoặc Write+.</summary>
    public static bool CanEditContent(ClaimsPrincipal user, Ticket ticket)
        => IsAuthor(user, ticket) || IsAtLeast(user, AccessLevel.Write);

    /// <summary>Đóng/mở: tác giả với ticket của mình; Triage+ mọi ticket.</summary>
    public static bool CanChangeState(ClaimsPrincipal user, Ticket ticket)
        => IsAuthor(user, ticket) || IsAtLeast(user, AccessLevel.Triage);

    /// <summary>Label, milestone, assignee người khác, duplicate, sub-issue, dependency: Triage+.</summary>
    public static bool CanTriage(ClaimsPrincipal user) => IsAtLeast(user, AccessLevel.Triage);

    /// <summary>Lock, pin, transfer, đổi type, kiểm duyệt comment người khác: Write+.</summary>
    public static bool CanWrite(ClaimsPrincipal user) => IsAtLeast(user, AccessLevel.Write);

    public static bool CanDelete(ClaimsPrincipal user) => IsAtLeast(user, AccessLevel.Admin);

    /// <summary>BR-LIFECYCLE-02: ticket khoá → chỉ Write+ được comment.</summary>
    public static bool CanComment(ClaimsPrincipal user, Ticket ticket)
        => user.HasPermission(Permissions.TicketComment) && (!ticket.IsLocked || IsAtLeast(user, AccessLevel.Write));

    /// <summary>BR-EDIT-01: tác giả comment (mọi role) hoặc Write+.</summary>
    public static bool CanModerateComment(ClaimsPrincipal user, Guid? commentAuthorId)
        => (commentAuthorId is { } a && a == user.GetUserId()) || IsAtLeast(user, AccessLevel.Write);

    public static void Ensure(bool allowed, string detail)
    {
        if (!allowed)
        {
            throw AppException.Forbidden(detail);
        }
    }

    /// <summary>
    /// BR-SOCIAL-05 — author association tính lúc render: OWNER (Admin), MEMBER (Triage+),
    /// CONTRIBUTOR (đã từng tạo ticket/comment khác trong project), FIRST_TIME_CONTRIBUTOR, NONE.
    /// </summary>
    public static string AssociationOf(AccessLevel level, bool hasPriorActivity)
        => level switch
        {
            AccessLevel.Admin => "OWNER",
            >= AccessLevel.Triage => "MEMBER",
            AccessLevel.Read => hasPriorActivity ? "CONTRIBUTOR" : "FIRST_TIME_CONTRIBUTOR",
            _ => "NONE"
        };
}
