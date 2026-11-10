namespace Aerochat.Connectivity.Rtc;

public static class RtcOptIn
{
    public const string EnvironmentVariable = "AEROCHAT_ENABLE_RTC";

    public static bool IsBlocked(bool serverConfigured, string? value) =>
        serverConfigured && !string.Equals(value, "1", StringComparison.Ordinal);
}
