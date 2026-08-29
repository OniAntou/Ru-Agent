namespace RuAgent.Runtime.Adapters.Speech;

public enum SpeechTransportFailure
{
    NoMatch,
    Timeout,
    Unavailable,
    Failed,
}

public class SpeechProviderException : Exception
{
    public SpeechProviderException(
        string code,
        bool retryable,
        Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}

public sealed class GoogleSpeechTransportException : Exception
{
    public GoogleSpeechTransportException(
        SpeechTransportFailure failure,
        string? providerMessage = null)
        : base("Google Speech transport failed.")
    {
        Failure = failure;
    }

    public SpeechTransportFailure Failure { get; }
}

public interface IGoogleSpeechTransport
{
    Task<string?> RecognizeAsync(
        ReadOnlyMemory<byte> wavBytes,
        string language,
        CancellationToken cancellationToken);
}
