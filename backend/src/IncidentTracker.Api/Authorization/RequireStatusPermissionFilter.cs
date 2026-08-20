using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using IncidentTracker.Api.Modules.Incidents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace IncidentTracker.Api.Authorization;

/// <summary>Request nào mang một trạng thái đích thì cài đặt interface này để filter đọc được.</summary>
public interface IStatusTargetRequest
{
    IncidentStatus TargetStatus { get; }
}

/// <summary>
/// Giải quyết xung đột thiết kế của <c>PATCH /api/incidents/{id}/status</c>: endpoint dùng chung
/// cho hai bước chuyển nhưng bước Resolved đòi thêm permission <c>incident.resolve</c>
/// (API-Incident-Status, FR-BIZ-03).
///
/// Policy tĩnh của authorization middleware không đọc được body nên không tự phân biệt được.
/// Filter này chạy sau model binding và <b>trước thân action</b>, short-circuit bằng 403 —
/// thỏa US-BIZ-03/AC-03 ("controller không chạy") mà vẫn không đặt if/else quyền trong
/// controller theo DRV-01.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireStatusPermissionAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => true;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider)
        => new RequireStatusPermissionFilter();
}

public sealed class RequireStatusPermissionFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var target = context.ActionArguments.Values
            .OfType<IStatusTargetRequest>()
            .Select(r => (IncidentStatus?)r.TargetStatus)
            .FirstOrDefault();

        if (target is null)
        {
            await next();
            return;
        }

        // DRV-05: permission của từng bước chuyển chỉ được khai báo trong state machine.
        // Hardcode lại ở đây sẽ tạo ra nguồn sự thật thứ hai và âm thầm sai khi thêm trạng thái.
        var required = IncidentStateMachine.RequiredPermission(target.Value);

        if (!context.HttpContext.User.HasPermission(required))
        {
            // Ghi thẳng response thay vì trả ObjectResult: content negotiation của MVC sẽ chọn
            // application/json và làm 403 này khác hình dạng với 403 do middleware sinh ra.
            await ProblemDetailsWriter.WriteAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                "Không đủ quyền",
                $"Chuyển sự cố sang '{target.Value}' yêu cầu permission '{required}'.",
                new Dictionary<string, object?>
                {
                    ["requiredPermission"] = required,
                    ["requestedStatus"] = target.Value.ToString()
                });

            context.Result = new EmptyResult();
            return;
        }

        await next();
    }
}
