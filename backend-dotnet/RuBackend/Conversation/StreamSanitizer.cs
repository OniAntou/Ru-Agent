using System.Text;

namespace RuBackend.Conversation;

public interface IStreamSanitizer
{
    string Append(string raw, string delta);

    string Visible(string raw);
}

public sealed class StreamSanitizer : IStreamSanitizer
{
    private static readonly string[] PrivateMarkerPrefixes =
    [
        "[[vts:",
        "[[voice_pace:",
        "[[emo:",
        "[search:",
        "[app_open:",
        "[volume:",
    ];

    public string Append(string raw, string delta)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.StartsWith(raw, StringComparison.Ordinal))
        {
            return delta;
        }

        if (raw.StartsWith(delta, StringComparison.Ordinal))
        {
            return raw;
        }

        var maximum = Math.Min(raw.Length, delta.Length);
        for (var length = maximum; length > 0; length--)
        {
            if (raw.AsSpan(raw.Length - length).SequenceEqual(delta.AsSpan(0, length)))
            {
                return raw + delta[length..];
            }
        }

        return raw + delta;
    }

    public string Visible(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var normalized = raw
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
        var markerIndex = PrivateMarkerPrefixes
            .Select(prefix => normalized.IndexOf(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (markerIndex >= 0)
        {
            return normalized[..markerIndex];
        }

        var heldCharacters = 0;
        foreach (var prefix in PrivateMarkerPrefixes)
        {
            var maximum = Math.Min(prefix.Length, normalized.Length);
            for (var length = 1; length <= maximum; length++)
            {
                if (normalized.EndsWith(prefix[..length], StringComparison.OrdinalIgnoreCase))
                {
                    heldCharacters = Math.Max(heldCharacters, length);
                }
            }
        }

        return heldCharacters == 0 ? normalized : normalized[..^heldCharacters];
    }
}
