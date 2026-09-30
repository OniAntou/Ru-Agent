using System.Text;

namespace RuBackend.Security;

/// <summary>
/// Reads a request body into a string without ever buffering more than the caller's bound.
/// </summary>
/// <remarks>
/// The endpoint used to bind the body through model binding, which buffers the whole
/// request before the handler can refuse it. This reader answers "too large" instead of
/// throwing, so each endpoint can choose its own status code and envelope.
/// </remarks>
internal static class BoundedRequestBodyReader
{
    private const int BufferSize = 8 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Returns <c>(true, text)</c> for a body within the bound, or <c>(false, null)</c>
    /// when the declared or actual body exceeds <paramref name="maxBytes"/>. A body that
    /// is present but empty reads as <c>(true, null)</c>.
    /// </summary>
    public static async Task<(bool Ok, string? Text)> ReadTextAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maxBytes < 0)
        {
            maxBytes = 0;
        }

        if (request.ContentLength is > 0 && request.ContentLength > maxBytes)
        {
            return (false, null);
        }

        using var buffer = new MemoryStream(
            request.ContentLength is >= 0 and <= int.MaxValue && request.ContentLength <= maxBytes
                ? checked((int)request.ContentLength.Value)
                : 0);
        var chunk = new byte[BufferSize];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length > maxBytes - read)
            {
                return (false, null);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (buffer.Length == 0)
        {
            return (true, null);
        }

        var text = StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
        return (true, text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text);
    }
}
