namespace RuAgent.Client;

public sealed class AgentApiException : Exception
{
    public string ErrorCode { get; }
    public int? StatusCode { get; }

    /// <summary>
    /// Run that already holds the conflicting workspace or session, when the host
    /// reported a busy conflict. Null for every other error.
    /// </summary>
    public string? BlockingRunId { get; }

    /// <summary>Status of the blocking run at conflict time, when reported.</summary>
    public string? BlockingRunStatus { get; }

    public AgentApiException(
        string errorCode,
        string message,
        int? statusCode = null,
        Exception? inner = null,
        string? blockingRunId = null,
        string? blockingRunStatus = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
        BlockingRunId = blockingRunId;
        BlockingRunStatus = blockingRunStatus;
    }

    public AgentApiException(string errorCode, int? statusCode = null)
        : base(errorCode)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }
}
