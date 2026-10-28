using System.Text;

namespace RuAgent.Broker.Tools.Web;

internal static class WebResponseReader
{
    public static async Task<string> ReadAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        using var memory = await ReadBodyAsync(response, limit, cancellationToken).ConfigureAwait(false);
        try
        {
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
            var encoding = string.IsNullOrWhiteSpace(charset)
                ? new UTF8Encoding(false, true)
                : Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            using var reader = new StreamReader(memory, encoding, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or DecoderFallbackException)
        {
            throw new InvalidDataException("web_encoding_invalid", error);
        }
    }

    /// <summary>The body as bytes, for binary documents such as PDF.</summary>
    public static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        using var memory = await ReadBodyAsync(response, limit, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static async Task<MemoryStream> ReadBodyAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > limit)
            throw new InvalidDataException("web_response_too_large");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var memory = new MemoryStream(Math.Min(limit, 64 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (memory.Length + count > limit)
            {
                await memory.DisposeAsync().ConfigureAwait(false);
                throw new InvalidDataException("web_response_too_large");
            }
            memory.Write(buffer, 0, count);
        }

        memory.Position = 0;
        return memory;
    }
}
