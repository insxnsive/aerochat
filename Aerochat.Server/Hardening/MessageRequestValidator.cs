namespace Aerochat.Server.Hardening;

public static class MessageRequestValidator
{
    public const int MaxBodyCharacters = 2000;

    /// <summary>
    /// Hard cap for the raw RefPayloadJson string accepted from clients. Bounds
    /// authenticated storage/memory use for arbitrary non-sticker payloads before
    /// anything is persisted or serialized to gateway subscribers.
    /// </summary>
    public const int MaxRefPayloadJsonCharacters = 2048;

    public static bool IsBodyWithinLimit(string body) => body.Length <= MaxBodyCharacters;
}
