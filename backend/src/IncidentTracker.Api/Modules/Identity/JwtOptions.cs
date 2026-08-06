using System.ComponentModel.DataAnnotations;

namespace IncidentTracker.Api.Modules.Identity;

/// <summary>Cấu hình JWT theo ADR-001. Signing key luôn đến từ biến môi trường, không hard-code.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required, MinLength(32, ErrorMessage = "Jwt:SigningKey phải dài tối thiểu 32 ký tự (256 bit).")]
    public string SigningKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = "gitissues-api";

    [Required]
    public string Audience { get; set; } = "gitissues-web";

    /// <summary>
    /// Tuổi thọ một access token. Vẫn ngắn hạn theo tinh thần ADR-001, nhưng người dùng không
    /// còn bị đá ra giữa chừng vì phiên được cấp lại khi còn thao tác (xem
    /// <see cref="SessionMaxHours"/> và <c>POST /api/auth/refresh</c>).
    ///
    /// Không nới quá 60: hệ thống chưa có danh sách thu hồi token — <c>jti</c> được sinh ra
    /// nhưng không nơi nào kiểm — nên đây chính là cửa sổ sống của một token bị lộ.
    /// </summary>
    [Range(1, 60)]
    public int ExpiryMinutes { get; set; } = 60;

    /// <summary>
    /// Trần tuyệt đối của một phiên, tính từ lần đăng nhập đầu. Hết mốc này thì dù còn thao tác
    /// cũng phải đăng nhập lại — nếu không, phiên trượt sẽ thành phiên vĩnh viễn.
    /// </summary>
    [Range(1, 168)]
    public int SessionMaxHours { get; set; } = 12;
}
