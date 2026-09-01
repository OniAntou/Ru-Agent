using System.Text;

namespace RuAgent.Runtime.Adapters.Models;

public sealed record ServerSentEvent(string? Event, string Data);

public static class BoundedSseReader
{
    private const int MaxLineBytes = 256 * 1024;
    private const int MaxEventBytes = 256 * 1024;
    private const int MaxTotalBytes = 5 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async IAsyncEnumerable<ServerSentEvent> ReadAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var readBuffer = new byte[4096];
        var lineBytes = new List<byte>(4096);
        var dataLines = new List<string>();
        string? currentEvent = null;
        long eventBytes = 0;
        long totalBytes = 0;
        var firstLine = true;
        var previousWasCarriageReturn = false;

        ServerSentEvent? ProcessLine()
        {
            string line;
            try
            {
                line = StrictUtf8.GetString(lineBytes.ToArray());
            }
            catch (DecoderFallbackException error)
            {
                throw new ModelProviderException("malformed_payload", false, error);
            }
            finally
            {
                lineBytes.Clear();
            }

            if (firstLine)
            {
                firstLine = false;
                if (line.Length > 0 && line[0] == '\uFEFF') line = line[1..];
            }

            if (line.Length == 0)
            {
                if (currentEvent is null && dataLines.Count == 0) return null;
                var result = new ServerSentEvent(currentEvent, string.Join("\n", dataLines));
                currentEvent = null;
                dataLines.Clear();
                eventBytes = 0;
                return result;
            }
            if (line[0] == ':') return null;

            var colon = line.IndexOf(':');
            string field;
            string value;
            if (colon < 0)
            {
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line[..colon];
                value = line[(colon + 1)..];
                if (value.Length > 0 && value[0] == ' ') value = value[1..];
            }

            if (field == "event")
            {
                currentEvent = value;
            }
            else if (field == "data")
            {
                var valueBytes = StrictUtf8.GetByteCount(value);
                var separatorBytes = dataLines.Count == 0 ? 0 : 1;
                if (eventBytes + separatorBytes + valueBytes > MaxEventBytes)
                    throw new ModelProviderException("response_too_large", false);
                eventBytes += separatorBytes + valueBytes;
                dataLines.Add(value);
            }
            return null;
        }

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(readBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelProviderException("timeout", true);
            }

            if (read == 0) break;
            totalBytes += read;
            if (totalBytes > MaxTotalBytes)
                throw new ModelProviderException("response_too_large", false);

            for (var index = 0; index < read; index++)
            {
                var value = readBuffer[index];
                if (value == (byte)'\r')
                {
                    var evt = ProcessLine();
                    if (evt is not null) yield return evt;
                    previousWasCarriageReturn = true;
                    continue;
                }
                if (value == (byte)'\n')
                {
                    if (previousWasCarriageReturn)
                    {
                        previousWasCarriageReturn = false;
                        continue;
                    }
                    var evt = ProcessLine();
                    if (evt is not null) yield return evt;
                    continue;
                }

                previousWasCarriageReturn = false;
                if (lineBytes.Count >= MaxLineBytes)
                    throw new ModelProviderException("response_too_large", false);
                lineBytes.Add(value);
            }
        }

        if (lineBytes.Count > 0) _ = ProcessLine();
        if (currentEvent is not null || dataLines.Count > 0)
            throw new ModelProviderException("malformed_payload", false);
    }
}
