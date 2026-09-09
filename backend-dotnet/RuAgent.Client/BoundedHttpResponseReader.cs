using System.Text;

namespace RuAgent.Client;

public static class BoundedHttpResponseReader
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<string> ReadAsync(HttpContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Headers.ContentLength is > MaximumBytes)
        {
            throw new AgentApiException("response_too_large", "Agent response exceeds the client limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(MaximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length > MaximumBytes - read)
                throw new AgentApiException("response_too_large", "Agent response exceeds the client limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return StrictUtf8.GetString(output.ToArray());
        }
        catch (DecoderFallbackException error)
        {
            throw new AgentApiException("invalid_response", "Agent response is not valid UTF-8.", inner: error);
        }
    }
}
