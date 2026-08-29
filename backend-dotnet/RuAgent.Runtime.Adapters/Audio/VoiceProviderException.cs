namespace RuAgent.Runtime.Adapters.Audio;

public class VoiceProviderException : Exception
{
    public VoiceProviderException(
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

public sealed class OmniVoiceProviderException : VoiceProviderException
{
    public OmniVoiceProviderException(
        string code,
        bool retryable,
        Exception? innerException = null)
        : base(code, retryable, innerException)
    {
    }
}
