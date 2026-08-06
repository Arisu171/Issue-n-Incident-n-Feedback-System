using System.Text;
using System.Text.RegularExpressions;
using IncidentTracker.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IncidentTracker.Api.Modules.Identity;

/// <summary>
/// Sinh <c>users.login</c> (tên ngắn cho <c>@mention</c>, Architecture v3.1 mục 5.1) theo quy
/// tắc của GitHub: chữ thường, số, gạch nối; không bắt đầu/kết thúc bằng gạch nối; tối đa 39 ký tự.
/// </summary>
public static partial class LoginNames
{
    public const int MaxLength = 39;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,38}$")]
    public static partial Regex ValidLogin();

    /// <summary>Phần trước <c>@</c> của email, chuẩn hoá về bộ ký tự hợp lệ.</summary>
    public static string FromEmail(string email)
    {
        var local = email.Split('@')[0].ToLowerInvariant();
        var sb = new StringBuilder(local.Length);
        foreach (var c in local)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var candidate = sb.ToString().Trim('-');
        if (candidate.Length == 0)
        {
            candidate = "user";
        }

        return candidate.Length > MaxLength ? candidate[..MaxLength].TrimEnd('-') : candidate;
    }

    /// <summary>Khử trùng bằng hậu tố số: <c>kien</c>, <c>kien-2</c>, <c>kien-3</c>...</summary>
    public static async Task<string> EnsureUniqueAsync(AppDbContext db, string baseLogin, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Login == baseLogin, ct))
        {
            return baseLogin;
        }

        for (var i = 2; i < 10_000; i++)
        {
            var suffix = $"-{i}";
            var stem = baseLogin.Length + suffix.Length > MaxLength
                ? baseLogin[..(MaxLength - suffix.Length)].TrimEnd('-')
                : baseLogin;
            var candidate = stem + suffix;
            if (!await db.Users.AnyAsync(u => u.Login == candidate, ct))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Không sinh được login duy nhất từ '{baseLogin}'.");
    }
}
