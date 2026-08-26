namespace Aerochat.Server.Hardening;

public static class GatewayOriginPolicy
{
    public static bool IsAllowed(string? origin, IReadOnlySet<string> allowedOrigins) =>
        string.IsNullOrWhiteSpace(origin)
        || allowedOrigins.Count == 0
        || allowedOrigins.Contains(origin.Trim());
}
