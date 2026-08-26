namespace Aerochat.Server.Hardening;

public static class MessageRequestValidator
{
    public const int MaxBodyCharacters = 2000;

    public static bool IsBodyWithinLimit(string body) => body.Length <= MaxBodyCharacters;
}
