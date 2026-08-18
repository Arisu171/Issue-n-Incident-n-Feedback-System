using System.Linq.Expressions;
using System.Security.Claims;
using IncidentTracker.Api.Domain;
using Microsoft.AspNetCore.Authorization;

namespace IncidentTracker.Api.Authorization;

/// <summary>
/// Thao tác trên một bản ghi cụ thể. Tách khỏi permission vì permission trả lời "loại hành
/// động nào", còn requirement này trả lời "trên bản ghi nào" — hai câu hỏi khác nhau nên
/// phải kiểm ở hai chỗ khác nhau.
/// </summary>
public sealed class ResourceOperationRequirement : IAuthorizationRequirement
{
    private ResourceOperationRequirement(string name) => Name = name;

    public string Name { get; }

    /// <summary>Đọc chi tiết một bản ghi.</summary>
    public static readonly ResourceOperationRequirement Read = new("read");

    /// <summary>Thay đổi một bản ghi — với Incident là chuyển trạng thái.</summary>
    public static readonly ResourceOperationRequirement Write = new("write");
}

/// <summary>
/// Nguồn sự thật duy nhất cho câu hỏi "user này được đụng vào bản ghi nào".
///
/// Cùng một quy tắc phải phục vụ hai hình dạng câu hỏi: handler hỏi trên một entity đã nạp,
/// còn endpoint danh sách phải hỏi ngay trong câu SQL. Viết hai lần ở hai nơi là cách chắc
/// chắn nhất để chúng lệch nhau sau vài sprint, nên cả hai đều gọi vào lớp này.
/// </summary>
public static class ResourceAccessRules
{
    // ---------------- Incident ----------------

    public static bool CanReadIncident(ClaimsPrincipal user, Guid reporterId, Guid? assigneeId)
    {
        if (user.HasPermission(Permissions.IncidentReadAll))
        {
            return true;
        }

        var uid = user.GetUserId();
        return reporterId == uid || assigneeId == uid;
    }

    /// <summary>
    /// Được gắn phản hồi vào sự cố này hay không.
    ///
    /// <b>Cố ý KHÔNG dùng <see cref="CanReadIncident"/>.</b> Trước đây hai điều đó trùng nhau nên
    /// mượn tạm cũng đúng; từ khi khách hàng được cấp <c>incident.read.all</c> thì "đọc được" đã
    /// rộng ra thành mọi sự cố trong project, còn "được gắn vào" thì không nên rộng theo — nếu
    /// không, một khách hàng đính phản hồi của mình vào sự cố của người khác được.
    ///
    /// Nhân viên phân loại hàng đợi (<c>feedback.read.all</c>) vẫn gắn được vào bất kỳ sự cố nào:
    /// đó chính là công việc của họ.
    /// </summary>
    public static bool CanAttachFeedbackTo(ClaimsPrincipal user, Guid reporterId, Guid? assigneeId)
    {
        if (user.HasPermission(Permissions.FeedbackReadAll))
        {
            return true;
        }

        var uid = user.GetUserId();
        return reporterId == uid || assigneeId == uid;
    }

    /// <summary>
    /// BR-BIZ-10 — chỉ người được giao mới chuyển được trạng thái. Sự cố chưa ai nhận thì
    /// người thao tác đầu tiên được đi tiếp và trở thành người phụ trách
    /// (<see cref="ShouldClaim"/>), nếu không mọi sự cố sẽ đứng im chờ admin gán tay.
    /// </summary>
    public static bool CanChangeIncidentStatus(ClaimsPrincipal user, Guid? assigneeId)
        => user.HasPermission(Permissions.IncidentManageAny)
           || assigneeId is null
           || assigneeId == user.GetUserId();

    /// <summary>
    /// Sự cố chưa ai nhận và người thao tác không có <see cref="Permissions.IncidentManageAny"/>
    /// thì tự nhận việc. Người có quyền đó (admin) được miễn: họ thao tác nhân danh vận hành,
    /// tự gán họ làm người phụ trách sẽ ghi sai người chịu trách nhiệm thật.
    /// </summary>
    public static bool ShouldClaim(ClaimsPrincipal user, Guid? assigneeId)
        => assigneeId is null && !user.HasPermission(Permissions.IncidentManageAny);

    /// <summary>
    /// Điều kiện lọc dịch được sang SQL cho endpoint danh sách. Trả <c>null</c> khi user
    /// thấy được tất cả — gọi bên ngoài chỉ cần bỏ qua bước <c>Where</c>.
    /// </summary>
    public static Expression<Func<Incident, bool>>? IncidentVisibilityFilter(ClaimsPrincipal user)
    {
        if (user.HasPermission(Permissions.IncidentReadAll))
        {
            return null;
        }

        var uid = user.GetUserId();
        return i => i.ReporterId == uid || i.AssigneeId == uid;
    }

    // ---------------- Feedback ----------------

    public static bool CanReadFeedback(ClaimsPrincipal user, Guid createdBy)
        => user.HasPermission(Permissions.FeedbackReadAll) || createdBy == user.GetUserId();

    /// <summary>
    /// BR-BIZ-12 — chỉ trả lời được phản hồi mà mình đọc được. Permission
    /// <c>feedback.respond</c> đã được kiểm ở tầng endpoint; quy tắc này trả lời câu hỏi còn
    /// lại: "trên bản ghi nào". Hiện trùng với quy tắc đọc, nhưng là quyết định riêng — nếu
    /// mai sau quy tắc ghi siết lại, chỗ sửa là đây chứ không phải handler.
    /// </summary>
    public static bool CanRespondFeedback(ClaimsPrincipal user, Guid createdBy)
        => CanReadFeedback(user, createdBy);

    public static Expression<Func<Feedback, bool>>? FeedbackVisibilityFilter(ClaimsPrincipal user)
    {
        if (user.HasPermission(Permissions.FeedbackReadAll))
        {
            return null;
        }

        var uid = user.GetUserId();
        return f => f.CreatedBy == uid;
    }
}

/// <summary>
/// Handler resource-based cho ENT-Incident. Chạy <b>sau</b> khi bản ghi đã được nạp, vì trước
/// đó hệ thống chưa biết ai là người phụ trách nên chưa thể quyết định được.
/// </summary>
public sealed class IncidentAuthorizationHandler
    : AuthorizationHandler<ResourceOperationRequirement, Incident>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ResourceOperationRequirement requirement,
        Incident resource)
    {
        var allowed = requirement.Name switch
        {
            "read" => ResourceAccessRules.CanReadIncident(
                context.User, resource.ReporterId, resource.AssigneeId),
            "write" => ResourceAccessRules.CanChangeIncidentStatus(context.User, resource.AssigneeId),
            _ => false
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Handler resource-based cho ENT-Feedback — chủ sở hữu là người đã gửi phản hồi.</summary>
public sealed class FeedbackAuthorizationHandler
    : AuthorizationHandler<ResourceOperationRequirement, Feedback>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ResourceOperationRequirement requirement,
        Feedback resource)
    {
        // Mỗi requirement một quy tắc riêng — không dùng quy tắc đọc trả lời hộ câu hỏi ghi,
        // và requirement lạ thì mặc định từ chối.
        var allowed = requirement.Name switch
        {
            "read" => ResourceAccessRules.CanReadFeedback(context.User, resource.CreatedBy),
            "write" => ResourceAccessRules.CanRespondFeedback(context.User, resource.CreatedBy),
            _ => false
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
