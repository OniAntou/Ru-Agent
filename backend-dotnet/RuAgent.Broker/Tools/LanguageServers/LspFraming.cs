using System.Globalization;
using System.Text;

namespace RuAgent.Broker.Tools.LanguageServers;

/// <summary>Bounded LSP Content-Length framing (ASCII headers, UTF-8 bodies).</summary>
public static class LspFraming
{
    public const int MaximumMessageBytes = 4 * 1024 * 1024;
    private const int MaximumHeaderBytes = 8192;

    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>(128);
        var match = 0;
        while (header.Count < MaximumHeaderBytes)
        {
            var one = new byte[1];
            var count = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (count == 0) return header.Count == 0 ? null : throw new InvalidDataException("lsp_protocol_error");
            header.Add(one[0]);
            match = (match, one[0]) switch
            {
                (0, 13) => 1, (1, 10) => 2, (2, 13) => 3, (3, 10) => 4,
                (_, 13) => 1, _ => 0,
            };
            if (match == 4) break;
        }

        if (match != 4) throw new InvalidDataException("lsp_protocol_error");
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        int? length = null;
        foreach (var line in lines)
        {
            var split = line.IndexOf(':');
            if (split <= 0) throw new InvalidDataException("lsp_protocol_error");
            var name = line[..split];
            var value = line[(split + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (length is not null || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                    || parsed < 0 || parsed > MaximumMessageBytes) throw new InvalidDataException("lsp_message_too_large");
                length = parsed;
            }
            else if (!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("lsp_protocol_error");
        }
        if (length is null) throw new InvalidDataException("lsp_protocol_error");
        var body = new byte[length.Value];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return body;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (body.Length > MaximumMessageBytes) throw new InvalidDataException("lsp_message_too_large");
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
