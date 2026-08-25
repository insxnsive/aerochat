namespace Aerochat.Connectivity.Auth;

public class AuthException : Exception
{
    public AuthException(string message) : base(message) { }

    public AuthException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class AuthUnavailableException : AuthException
{
    public AuthUnavailableException() : base("Authentication is not configured.") { }
}
