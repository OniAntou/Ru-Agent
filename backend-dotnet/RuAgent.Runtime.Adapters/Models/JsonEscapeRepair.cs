namespace RuAgent.Runtime.Adapters.Models;

/// <summary>
/// Tolerates two sloppy-emitter shapes in streamed JSON without ever guessing at
/// meaning: lone backslashes that cannot start a valid escape, and chunks cut
/// mid-string. Both shapes are common when a model streams Windows paths
/// (<c>C:\Users\...</c>) through an OpenAI-compatible gateway.
///
/// Safety contract, read carefully: the repair only doubles a backslash that starts
/// an escape JSON forbids (<c>\U</c>, <c>\s</c>, ...). Such input has no valid
/// reading at all, so the repaired value is the unique sane one. Subsequences that
/// form <i>valid</i> escapes (<c>\t</c>, <c>\n</c>, ...) are never touched even
/// though a sloppy emitter may have meant a literal backslash there: a tab in
/// <c>C:\temp</c> is genuinely ambiguous and no repair can resolve it. Likewise a
/// chunk that ends mid-string is never repaired, only buffered by the caller,
/// because its continuation may still arrive.
/// </summary>
public static class JsonEscapeRepair
{
    /// <summary>
    /// True when the candidate ends inside a string, with unbalanced structure, or
    /// with a lone trailing backslash: it may be a prefix of a larger chunk, so
    /// repairing it now could corrupt the continuation. Callers buffer and retry
    /// with more input instead.
    /// </summary>
    public static bool LooksTruncated(string candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Scan(candidate).Truncated;
    }

    /// <summary>
    /// Rebuilds <paramref name="candidate"/> with every lone backslash doubled.
    /// Returns false (leaving <paramref name="repaired"/> empty) when the input
    /// looks truncated or needs no repair. Callers re-parse the result with the
    /// normal JSON parser, so a repair that still does not parse fails exactly
    /// where it always did.
    /// </summary>
    public static bool TryRepairInvalidEscapes(string candidate, out string repaired)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        repaired = string.Empty;
        var scan = Scan(candidate);
        if (scan.Truncated || !scan.HasRepairs)
        {
            return false;
        }

        var builder = new System.Text.StringBuilder(candidate.Length + scan.RepairCount);
        var i = 0;
        while (i < candidate.Length)
        {
            var c = candidate[i];
            if (c == '\\' && i + 1 < candidate.Length)
            {
                if (IsLoneBackslash(candidate, i))
                {
                    // Double it; the escaped character is copied below like content.
                    builder.Append('\\');
                    builder.Append(c);
                    builder.Append(candidate[i + 1]);
                    i += 2;
                    continue;
                }
                else if (candidate[i + 1] == 'u')
                {
                    // A valid \uXXXX sequence: Scan only lets complete ones through.
                    builder.Append(candidate, i, 6);
                    i += 6;
                    continue;
                }
                else
                {
                    // A valid two-character escape: copy both characters.
                    builder.Append(c);
                    builder.Append(candidate[i + 1]);
                    i += 2;
                    continue;
                }
            }

            builder.Append(c);
            i++;
        }

        repaired = builder.ToString();
        return true;
    }

    private static bool IsValidEscapeHead(char c) =>
        c is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't';

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static bool IsLoneBackslash(string candidate, int index)
    {
        // Caller guarantees candidate[index] == '\\' and index + 1 < length.
        // A \u sequence reaching the rebuild always carries four hex digits: Scan
        // classifies anything shorter or non-hex as truncated first, so \u is never
        // repaired here, only passed through.
        var next = candidate[index + 1];
        return next != 'u' && !IsValidEscapeHead(next);
    }

    private static (bool Truncated, bool HasRepairs, int RepairCount) Scan(string candidate)
    {
        var inString = false;
        var depth = 0;
        var repairs = 0;
        var i = 0;
        while (i < candidate.Length)
        {
            var c = candidate[i];
            if (!inString)
            {
                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{' || c == '[')
                {
                    depth++;
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth < 0)
                    {
                        return (false, false, 0);
                    }
                }

                i++;
                continue;
            }

            if (c != '\\')
            {
                if (c == '"')
                {
                    inString = false;
                }

                i++;
                continue;
            }

            if (i + 1 >= candidate.Length)
            {
                return (true, false, 0);
            }

            var next = candidate[i + 1];
            if (IsValidEscapeHead(next))
            {
                i += 2;
                continue;
            }

            if (next == 'u')
            {
                var hex = 0;
                while (hex < 4 && i + 2 + hex < candidate.Length && IsHex(candidate[i + 2 + hex]))
                {
                    hex++;
                }

                if (hex < 4)
                {
                    // \u cut off or malformed: either more input may complete it or
                    // the chunk is corrupt. Never repair; the caller decides.
                    return (true, false, 0);
                }

                i += 6;
                continue;
            }

            repairs++;
            i += 2;
        }

        if (inString || depth != 0)
        {
            return (true, false, 0);
        }

        return (false, repairs > 0, repairs);
    }
}
