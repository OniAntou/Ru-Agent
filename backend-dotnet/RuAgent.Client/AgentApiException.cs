namespace RuAgent.Client;

public sealed class AgentApiException : Exception
{
    public string ErrorCode { get; }
    public int? StatusCode { get; }

    public AgentApiException(string errorCode, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public AgentApiException(string errorCode, int? statusCode = null)
        : base(errorCode)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}
