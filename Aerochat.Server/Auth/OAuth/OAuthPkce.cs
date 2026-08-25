using System.Security.Cryptography;
using System.Text;

namespace Aerochat.Server.Auth.OAuth;

public static class OAuthPkce
{
    public static string CreateVerifier()
    {
        return Base64Url(RandomNumberGenerator.GetBytes(32));
    }

    public static string CreateChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        byte[] digest = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(digest);
    }

    public static string Base64Url(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
