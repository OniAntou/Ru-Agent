namespace RuAgent.Runtime.Adapters.Models;

public sealed class ModelProviderException : Exception
{
    public ModelProviderException(string code, bool retryable, Exception? innerException = null, TimeSpan? retryAfter = null)
        : base($"model_provider_{code}", innerException)
    {
        Code = code;
        Retryable = retryable;
        RetryAfter = retryAfter is { } delay && delay > TimeSpan.Zero
            ? delay
            : null;
    }

    public string Code { get; }

    public bool Retryable { get; }

    /// <summary>Bounded server retry guidance. It never contains response content.</summary>
    public TimeSpan? RetryAfter { get; }
}
