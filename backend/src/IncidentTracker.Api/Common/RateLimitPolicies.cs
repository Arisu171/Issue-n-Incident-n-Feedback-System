namespace IncidentTracker.Api.Common;

/// <summary>
/// Mục 6.6 đánh dấu rate limit login là [XÁC NHẬN]. Nhóm chốt: cửa sổ cố định 1 phút,
/// mặc định 10 lần thử cho mỗi IP, chỉnh được qua <c>RATELIMIT__LOGINPERMINUTE</c>.
/// </summary>
public static class RateLimitPolicies
{
    public const string Login = "login";

    /// <summary>
    /// Đăng ký siết chặt hơn đăng nhập: một người thật chỉ đăng ký vài lần trong đời, còn kịch
    /// bản tự động thì tạo hàng loạt tài khoản rác.
    /// </summary>
    public const string Register = "register";

    /// <summary>BR-SEC-03 (v3.1) — đọc module Tickets, theo user.</summary>
    public const string TicketRead = "ticket-read";

    /// <summary>BR-SEC-03 (v3.1) — ghi module Tickets, theo user.</summary>
    public const string TicketWrite = "ticket-write";
}

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public int LoginPerMinute { get; set; } = 10;

    public int RegisterPerHour { get; set; } = 5;
}
