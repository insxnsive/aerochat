using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Aerochat.Server.Auth;

public sealed class SessionService
{
    private const string Issuer = "aerochat-server";
    private readonly byte[] _signingKey;
    private readonly TimeProvider _clock;
    private readonly JsonWebTokenHandler _tokenHandler = new();

    public SessionService(byte[] signingKey, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(clock);
        if (signingKey.Length < 32)
        {
            throw new ArgumentException("HS256 signing keys must be at least 256 bits.", nameof(signingKey));
        }

        _signingKey = signingKey.ToArray();
        _clock = clock;
    }

    public TimeSpan DefaultTtl { get; } = TimeSpan.FromHours(1);

    public string Issue(Identity identity) => Issue(identity, DefaultTtl);

    public string Issue(Identity identity, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(identity);

        DateTimeOffset issuedAt = _clock.GetUtcNow();
        DateTimeOffset expiresAt = issuedAt.Add(ttl);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            IssuedAt = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(_signingKey),
                SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["provider"] = identity.Provider,
                ["provider_user_id"] = identity.ProviderUserId,
                ["display_name"] = identity.DisplayName,
                ["sub"] = $"{identity.Provider}:{identity.ProviderUserId}"
            }
        };

        return _tokenHandler.CreateToken(descriptor);
    }

    public SessionClaims? Validate(string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            TokenValidationResult result = _tokenHandler.ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = Issuer,
                ValidateAudience = false,
                RequireAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(_signingKey),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero,
                LifetimeValidator = ValidateLifetime
            }).GetAwaiter().GetResult();

            if (!result.IsValid || result.ClaimsIdentity is null || result.SecurityToken is null)
            {
                return null;
            }

            var identity = result.ClaimsIdentity;
            string? provider = identity.FindFirst("provider")?.Value;
            string? providerUserId = identity.FindFirst("provider_user_id")?.Value;
            string? displayName = identity.FindFirst("display_name")?.Value;
            if (provider is null || providerUserId is null || displayName is null)
            {
                return null;
            }

            return new SessionClaims(
                provider,
                providerUserId,
                displayName,
                new DateTimeOffset(result.SecurityToken.ValidTo.ToUniversalTime()));
        }
        catch (SecurityTokenException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private bool ValidateLifetime(
        DateTime? notBefore,
        DateTime? expires,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        return (!notBefore.HasValue || now >= notBefore.Value)
            && expires.HasValue
            && now < expires.Value;
    }
}
