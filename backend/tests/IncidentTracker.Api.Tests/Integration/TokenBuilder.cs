using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace IncidentTracker.Api.Tests.Integration;

/// <summary>
/// Sinh token bất thường cho ma trận 401 của TC-005 và NFR-SEC-01: hết hạn, sai issuer,
/// sai khóa ký. Đây là các trường hợp không thể tạo qua endpoint login.
/// </summary>
public static class TokenBuilder
{
    private const string Issuer = "incident-tracker-api";
    private const string Audience = "incident-tracker-web";

    public static string Expired() => Build(
        ApiFactory.SigningKey, Issuer, Audience,
        notBefore: DateTime.UtcNow.AddMinutes(-30), expires: DateTime.UtcNow.AddMinutes(-10));

    public static string WrongIssuer() => Build(
        ApiFactory.SigningKey, "ke-tan-cong", Audience,
        DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15));

    public static string WrongSigningKey() => Build(
        "khoa-gia-mao-cung-dai-tren-32-ky-tu-de-hop-le", Issuer, Audience,
        DateTime.UtcNow, DateTime.UtcNow.AddMinutes(15));

    private static string Build(string key, string issuer, string audience,
        DateTime notBefore, DateTime expires)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("perm", "user.read")
        };

        var token = new JwtSecurityToken(
            issuer, audience, claims, notBefore, expires,
            new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
