using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace BgiCoordinatorServer.Services;

/// <summary>Upload-only credential is never an administrator identity. CSRF is per signed-in cookie session.</summary>
public sealed class RoutePackageAccess
{
    private readonly byte[] _csrfKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[]? _uploadDigest;
    public const string SessionClaim = "admin-cookie-session";

    public RoutePackageAccess(string? uploadToken)
    {
        if (uploadToken is { Length: >= 32 and <= 1024 } && !string.IsNullOrWhiteSpace(uploadToken))
            _uploadDigest = SHA256.HashData(Encoding.UTF8.GetBytes(uploadToken));
    }

    public bool VerifyUploadToken(string? token)
        => _uploadDigest is not null && token is { Length: >= 32 and <= 1024 }
            && CryptographicOperations.FixedTimeEquals(_uploadDigest, SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public string IssueCsrf(ClaimsPrincipal principal, DateTimeOffset? now = null)
    {
        var session = principal.FindFirst(SessionClaim)?.Value;
        if (principal.Identity?.IsAuthenticated != true || session is not { Length: 32 })
            throw new RoutePackageException(401, "请重新登录管理员，以获取独立会话。");
        var payload = (now ?? DateTimeOffset.UtcNow).AddMinutes(30).ToUnixTimeSeconds() + ":" + session;
        return payload + ":" + Convert.ToHexString(HMACSHA256.HashData(_csrfKey, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public bool VerifyCsrf(ClaimsPrincipal principal, string? token, DateTimeOffset? now = null)
    {
        if (principal.Identity?.IsAuthenticated != true || token is null || token.Length > 160) return false;
        var pieces = token.Split(':');
        var current = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        if (pieces.Length != 3 || !long.TryParse(pieces[0], out var expires) || expires < current || expires > current + 1800
            || principal.FindFirst(SessionClaim)?.Value != pieces[1]) return false;
        var signature = HMACSHA256.HashData(_csrfKey, Encoding.UTF8.GetBytes(pieces[0] + ":" + pieces[1]));
        try { return CryptographicOperations.FixedTimeEquals(signature, Convert.FromHexString(pieces[2])); }
        catch (FormatException) { return false; }
    }
}
