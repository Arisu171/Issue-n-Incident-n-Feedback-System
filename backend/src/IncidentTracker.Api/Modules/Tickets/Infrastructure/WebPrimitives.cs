using System.Security.Claims;
using System.Text;
using IncidentTracker.Api.Authorization;
using IncidentTracker.Api.Common;
using IncidentTracker.Api.Domain;
using Microsoft.Net.Http.Headers;

namespace IncidentTracker.Api.Modules.Tickets.Infrastructure;

/// <summary>BR-CONC-01: <c>ETag: "v{version}"</c> và kiểm tra <c>If-Match</c> → 412.</summary>
public static class EntityTags
{
    public static string Of(int version) => $"\"v{version}\"";

    public static void SetETag(HttpResponse response, int version)
        => response.Headers[HeaderNames.ETag] = Of(version);

    /// <summary>
    /// Ném 412 khi client gửi <c>If-Match</c> không khớp phiên bản hiện tại. Thiếu header thì bỏ
    /// qua (last-write-wins — giống GitHub, vốn không bắt buộc If-Match); <c>*</c> luôn khớp.
    /// </summary>
    public static void EnsureIfMatch(HttpRequest request, int currentVersion)
    {
        if (!request.Headers.TryGetValue(HeaderNames.IfMatch, out var values) || values.Count == 0)
        {
            return;
        }

        var raw = values.ToString();
        if (raw.Trim() == "*")
        {
            return;
        }

        var expected = Of(currentVersion);
        var matches = raw.Split(',').Select(v => v.Trim())
            .Any(v => v == expected || v == expected.Trim('"') || v == "W/" + expected);

        if (!matches)
        {
            throw new AppException(StatusCodes.Status412PreconditionFailed,
                "Phiên bản đã thay đổi",
                $"Ticket đã được người khác cập nhật (phiên bản hiện tại {expected}). Tải lại rồi thử lại.",
                new Dictionary<string, object?> { ["currentVersion"] = currentVersion, ["etag"] = expected });
        }
    }
}

/// <summary>BR-SCALE-01: cursor keyset base64url trên <c>sequence</c> (hoặc cặp sort key).</summary>
public static class Cursor
{
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 100;

    public static string Encode(long value) => Encode(value.ToString());

    public static string Encode(string raw)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string? DecodeRaw(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }
        try
        {
            var s = cursor.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException)
        {
            throw AppException.BadRequest("cursor không hợp lệ.");
        }
    }

    public static long? DecodeSequence(string? cursor)
    {
        var raw = DecodeRaw(cursor);
        if (raw is null)
        {
            return null;
        }
        return long.TryParse(raw, out var v) ? v : throw AppException.BadRequest("cursor không hợp lệ.");
    }

    public static int ClampPageSize(int? perPage)
        => perPage is null or <= 0 ? DefaultPageSize : Math.Min(perPage.Value, MaxPageSize);
}

/// <summary>
/// BR-SEC-06: bộ lọc visibility dùng chung cho REST, Search, Notification, Webhook, SignalR.
/// </summary>
public static class TimelineAccess
{
    /// <summary>Support/Responder/Admin — mức Triage trở lên — thấy được INTERNAL.</summary>
    public static bool CanSeeInternal(ClaimsPrincipal user)
        => user.HasPermission(Permissions.TicketInternalNote);

    public static bool CanSee(ClaimsPrincipal user, TicketEvent evt)
        => evt.Visibility switch
        {
            EventVisibility.Public => true,
            EventVisibility.Internal => CanSeeInternal(user),
            EventVisibility.ActorOnly => evt.ActorId == user.GetUserId(),
            _ => false
        };

    /// <summary>Điều kiện dịch được sang SQL cho <c>GET /timeline</c>.</summary>
    public static IQueryable<TicketEvent> ApplyVisibility(IQueryable<TicketEvent> q, ClaimsPrincipal user)
    {
        var uid = user.GetUserId();
        var internalOk = CanSeeInternal(user);

        return internalOk
            ? q.Where(e => e.Visibility != EventVisibility.ActorOnly || e.ActorId == uid)
            : q.Where(e => e.Visibility == EventVisibility.Public
                           || (e.Visibility == EventVisibility.ActorOnly && e.ActorId == uid));
    }

    /// <summary>Mức quyền GitHub suy từ permission (mục 10.1).</summary>
    public static AccessLevel LevelOf(ClaimsPrincipal user)
    {
        if (user.HasPermission(Permissions.TicketDelete)) return AccessLevel.Admin;
        if (user.HasPermission(Permissions.TicketWrite)) return AccessLevel.Write;
        if (user.HasPermission(Permissions.TicketTriage)) return AccessLevel.Triage;
        if (user.HasPermission(Permissions.TicketRead)) return AccessLevel.Read;
        return AccessLevel.None;
    }
}

public enum AccessLevel { None = 0, Read = 1, Triage = 2, Write = 3, Admin = 4 }
