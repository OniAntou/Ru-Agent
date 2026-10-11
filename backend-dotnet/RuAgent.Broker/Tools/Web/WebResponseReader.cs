using System.Text;

namespace RuAgent.Broker.Tools.Web;

internal static class WebResponseReader
{
    public static async Task<string> ReadAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long length && length > limit)
            throw new InvalidDataException("web_response_too_large");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(Math.Min(limit, 64 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (memory.Length + count > limit)
                throw new InvalidDataException("web_response_too_large");
            memory.Write(buffer, 0, count);
        }

        try
        {
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
            var encoding = string.IsNullOrWhiteSpace(charset)
                ? new UTF8Encoding(false, true)
                : Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            memory.Position = 0;
            using var reader = new StreamReader(memory, encoding, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ArgumentException or DecoderFallbackException)
        {
            throw new InvalidDataException("web_encoding_invalid", error);
        }
    }
}
