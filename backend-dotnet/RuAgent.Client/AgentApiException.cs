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

    public string? PresetSessionId { get; }

    public long? CurrentPresetVersion { get; }

    /// <summary>True when a failed create acknowledgement cannot prove whether the Host committed the run.</summary>
    public bool IsCreateOutcomeUnknown { get; }

    public AgentApiException(
        string errorCode,
        string message,
        int? statusCode = null,
        Exception? inner = null,
        string? blockingRunId = null,
        string? blockingRunStatus = null,
        string? presetSessionId = null,
        long? currentPresetVersion = null,
        bool isCreateOutcomeUnknown = false)
        : base(message, inner)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
        BlockingRunId = blockingRunId;
        BlockingRunStatus = blockingRunStatus;
        PresetSessionId = presetSessionId;
        CurrentPresetVersion = currentPresetVersion;
        IsCreateOutcomeUnknown = isCreateOutcomeUnknown;
    }

    public AgentApiException(string errorCode, int? statusCode = null)
        : base(errorCode)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
        IsCreateOutcomeUnknown = false;
    }
}
