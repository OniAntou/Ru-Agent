namespace RuAgent.Runtime.Adapters.Observation;

public sealed class ObservationProviderException : Exception
{
    public ObservationProviderException(
        string code,
        bool retryable,
        Exception? innerException = null,
        string? reason = null)
        : base(code, innerException)
    {
        Code = code;
        Retryable = retryable;
        Reason = reason;
    }

    public string Code { get; }

    public bool Retryable { get; }

    public string? Reason { get; }
}
