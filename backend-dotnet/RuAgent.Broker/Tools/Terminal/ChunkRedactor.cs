using System.Text;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Redacts known credential values from streamed terminal output, including a
/// secret split across two chunks.
/// </summary>
/// <remarks>
/// Only a trailing run that could still become a secret is held back: the
/// longest suffix of the pending text that is a prefix of some seed. Holding a
/// fixed <c>longest seed - 1</c> characters instead kept every short output
/// (and the tail of every long one) until the process exited, which silently
/// turned the live tool-output stream into an end-of-command batch whenever the
/// host environment carried a credential. A short run that cannot start a
/// secret is emitted immediately; a partial secret is held and completed by the
/// next chunk.
///
/// The hold is not capped: a seed longer than any configured limit would
/// otherwise be emitted piece by piece and never match again. The suffix search
/// is linear in practice, so a long configured credential costs no fixed
/// stalling bound.
/// </remarks>
internal sealed class ChunkRedactor(IReadOnlyList<string> seeds)
{
    private readonly string[] _seeds = seeds
        .Where(seed => !string.IsNullOrEmpty(seed))
        .OrderByDescending(seed => seed.Length)
        .ToArray();

    private string _pending = string.Empty;

    public bool RedactedAnything { get; private set; }

    public string Push(string chunk)
    {
        if (_seeds.Length == 0)
        {
            return chunk;
        }

        _pending += chunk;
        // A suffix that matches a whole seed is still withheld here: the cursor
        // passed to Drain only covers text before it, so the match is emitted
        // once the next chunk (or Flush) proves it is not part of a longer value.
        var holdBack = LongestPossibleSecretSuffix();
        return Drain(_pending.Length - holdBack);
    }

    public string Flush()
    {
        return Drain(_pending.Length);
    }

    /// <summary>
    /// Length of the longest suffix of the pending text that is a prefix of any
    /// configured seed. That run is the only text a following chunk could turn
    /// into a credential, so it is the only text worth withholding.
    /// </summary>
    private int LongestPossibleSecretSuffix()
    {
        var best = 0;
        foreach (var seed in _seeds)
        {
            var first = seed[0];
            // A suffix longer than the seed cannot be its prefix.
            var start = Math.Max(0, _pending.Length - seed.Length);
            for (var position = start; position < _pending.Length; position++)
            {
                if (_pending[position] != first)
                {
                    continue;
                }

                var length = _pending.Length - position;
                if (length <= best)
                {
                    // Later positions are shorter; this seed cannot improve best.
                    break;
                }

                if (_pending.AsSpan(position).SequenceEqual(seed.AsSpan(0, length)))
                {
                    // The leftmost match is the longest suffix for this seed.
                    best = length;
                    break;
                }
            }
        }

        return best;
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

            if (matchedSeed is null)
            {
                output.Append(_pending, cursor, safeLength - cursor);
                cursor = safeLength;
                break;
            }

            // Match against the entire pending buffer before emitting anything:
            // a credential may overlap the matched one and extend past it.
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

            if (matchEnd > safeLength)
            {
                // The match reaches into the withheld tail, where a later chunk can
                // still extend an overlapping credential. Emitting the replacement
                // now would consume the first characters of that second secret and
                // let its remainder stream unredacted, so emit only the text before
                // the match and keep everything from it pending.
                break;
            }

            output.Append(_pending, cursor, matchIndex - cursor);
            output.Append("[redacted]");
            RedactedAnything = true;
            cursor = matchEnd;
        }

        _pending = _pending[cursor..];
        return output.ToString();
    }
}
