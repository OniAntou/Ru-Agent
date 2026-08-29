namespace RuAgent.Runtime.Adapters.Devices;

public sealed class VtubeStudioProviderException : Exception
{
    public VtubeStudioProviderException(
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
