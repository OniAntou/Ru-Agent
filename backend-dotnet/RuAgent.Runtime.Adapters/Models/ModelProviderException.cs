namespace RuAgent.Runtime.Adapters.Models;

public sealed class ModelProviderException : Exception
{
    public ModelProviderException(string code, bool retryable, Exception? innerException = null)
        : base($"model_provider_{code}", innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}
