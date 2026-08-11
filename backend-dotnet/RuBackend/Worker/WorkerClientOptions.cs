namespace RuBackend.Worker;

public sealed class WorkerClientOptions
{
    public const int CurrentProtocolVersion = 1;
    public const int DefaultMaxMessageBytes = 8 * 1024 * 1024;

    public Uri Uri { get; init; } = new("ws://127.0.0.1:5053/worker/v1");

    public string Token { get; init; } = string.Empty;

    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;

    public int MaxMessageBytes { get; init; } = DefaultMaxMessageBytes;

    internal void Validate()
    {
        if (!Uri.IsAbsoluteUri
            || !Uri.IsLoopback
            || (Uri.Scheme != "ws" && Uri.Scheme != "wss"))
        {
            throw new ArgumentException("Worker URI must be an absolute loopback WebSocket URI.", nameof(Uri));
        }

        if (string.IsNullOrWhiteSpace(Token))
        {
            throw new ArgumentException("Worker token is required.", nameof(Token));
        }

        if (ProtocolVersion != CurrentProtocolVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProtocolVersion),
                "Only worker protocol version 1 is supported.");
        }

        if (MaxMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMessageBytes));
        }
    }
}

public sealed class WorkerUnavailableException : Exception
{
    public WorkerUnavailableException(Exception? innerException = null)
        : base("Python runtime worker is unavailable.", innerException)
    {
    }
}
