namespace Aerochat.Server.Auth.OAuth;

public sealed class OAuthFlowException : Exception
{
    public OAuthFlowException(int statusCode, string errorCode)
        : base(errorCode)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public OAuthFlowException(int statusCode, string errorCode, Exception innerException)
        : base(errorCode, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }

    public string ErrorCode { get; }
}

public sealed class OAuthProviderException : Exception
{
    public OAuthProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OAuthProviderException(string message)
        : base(message)
    {
    }
}

public sealed class OAuthFlowCapacityException : Exception
{
    public OAuthFlowCapacityException(string message)
        : base(message)
    {
    }
}
