using System.Text;

namespace RuAgent.Broker.Tools.Terminal;

internal sealed class ChunkRedactor(IReadOnlyList<string> seeds)
{
    private readonly string[] _seeds = seeds.OrderByDescending(seed => seed.Length).ToArray();
    private readonly int _holdBack = seeds.Count == 0 ? 0 : seeds.Max(seed => seed.Length) - 1;
    private string _pending = string.Empty;

    public bool RedactedAnything { get; private set; }

    public string Push(string chunk)
    {
        if (_seeds.Length == 0)
        {
            return chunk;
        }

        _pending += chunk;
        var safeLength = Math.Max(0, _pending.Length - _holdBack);
        return Drain(safeLength);
    }

    public string Flush()
    {
        return Drain(_pending.Length);
    }

    private string Drain(int safeLength)
    {
        if (safeLength == 0)
        {
            return string.Empty;
        }

        var output = new StringBuilder();
        var cursor = 0;
        while (cursor < safeLength)
        {
            var matchIndex = safeLength;
            string? matchedSeed = null;
            foreach (var seed in _seeds)
            {
                var index = _pending.IndexOf(seed, cursor, StringComparison.Ordinal);
                if (index >= cursor && index < matchIndex)
                {
                    matchIndex = index;
                    matchedSeed = seed;
                }
            }

            output.Append(_pending, cursor, matchIndex - cursor);
            if (matchedSeed is null)
            {
                cursor = safeLength;
                break;
            }

            // Match against the entire pending buffer before emitting its safe
            // prefix: a credential may extend across that prefix's boundary.
            var matchEnd = matchIndex + matchedSeed.Length;
            // Different credentials can overlap. Consume their complete union
            // so replacing the first cannot expose the suffix of another.
            for (var position = matchIndex + 1; position < matchEnd; position++)
            {
                foreach (var seed in _seeds)
                {
                    if (_pending.AsSpan(position).StartsWith(seed, StringComparison.Ordinal))
                    {
                        matchEnd = Math.Max(matchEnd, position + seed.Length);
                    }
                }
            }

            output.Append("[redacted]");
            RedactedAnything = true;
            cursor = matchEnd;
        }

        _pending = _pending[cursor..];
        return output.ToString();
    }
}
