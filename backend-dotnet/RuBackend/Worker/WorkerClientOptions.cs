namespace RuBackend.Worker;

public sealed record AgentWorkerClientOptions(Uri Uri, string Token)
{
    public const int ProtocolVersion = 2;
    public const int DefaultMaxMessageBytes = 8 * 1024 * 1024;

    public int MaxMessageBytes { get; init; } = DefaultMaxMessageBytes;

    public void Validate() => WorkerClientOptionValidation.Validate(
        Uri,
        Token,
        "/worker/v2",
        MaxMessageBytes);
}

public sealed record DeviceWorkerClientOptions(Uri Uri, string Token)
{
    public const int ProtocolVersion = 1;
    public const int DefaultMaxMessageBytes = 8 * 1024 * 1024;

    public int MaxMessageBytes { get; init; } = DefaultMaxMessageBytes;

    public void Validate() => WorkerClientOptionValidation.Validate(
        Uri,
        Token,
        "/worker/v1",
        MaxMessageBytes);
}

internal static class WorkerClientOptionValidation
{
    public static void Validate(
        Uri? uri,
        string? token,
        string expectedPath,
        int maxMessageBytes)
    {
        if (uri is null
            || !uri.IsAbsoluteUri
            || !uri.IsLoopback
            || (uri.Scheme != "ws" && uri.Scheme != "wss")
            || !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Worker URI must be an absolute loopback WebSocket URI at {expectedPath}.",
                nameof(uri));
        }

        if (string.IsNullOrWhiteSpace(token)
            || token.Length > 4096
            || token.Any(character => character > 127 || char.IsControl(character)))
        {
            throw new ArgumentException("Worker token is invalid.", nameof(token));
        }

        if (maxMessageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        }
    }
}

public sealed class WorkerUnavailableException : Exception
{
    public WorkerUnavailableException(Exception? innerException = null)
        : base("Runtime worker is unavailable.", innerException)
    {
    }
}
