namespace Aerochat.Server.Gateway;

public sealed class GatewayOptions
{
    public string? InstanceId { get; init; }
    public int QueueCapacity { get; init; } = 256;
    public int ReplayCapacity { get; init; } = 4096;
    public int MaxFrameBytes { get; init; } = 256 * 1024;
    public IReadOnlySet<string> AllowedOrigins { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    internal string ResolveInstanceId()
    {
        if (InstanceId is null)
        {
            return Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(InstanceId)
            || InstanceId.Any(char.IsWhiteSpace)
            || InstanceId.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "InstanceId must be non-whitespace and cannot contain ':'.",
                nameof(InstanceId));
        }

        return InstanceId;
    }
}
