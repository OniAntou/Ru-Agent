using System.Text;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools.Git;

/// <summary>
/// Pure bounded parser for `git status --porcelain=v2 --branch` text. Malformed lines are
/// reported per-line instead of failing the whole parse, and the entry count is capped so
/// a pathological repository cannot produce unbounded state.
/// </summary>
public static class GitStatusParser
{
    public const int MaxEntries = 2000;

    public static RunGitStatusParseResult Parse(string porcelainV2Text)
    {
        var branch = new RunGitBranchHeader(null, string.Empty, IsDetached: false);
        var entries = new List<RunGitStatusEntry>();
        var errors = new List<string>();
        var truncated = false;

        foreach (var rawLine in porcelainV2Text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (entries.Count >= MaxEntries && !IsHeaderLine(line))
            {
                truncated = true;
                break;
            }

            switch (line[0])
            {
                case '#':
                    branch = ParseHeader(branch, line);
                    break;
                case '1' when line.Length > 1 && line[1] == ' ':
                    {
                        var ordinary = ParseOrdinaryEntry(line);
                        if (ordinary is not null)
                        {
                            entries.Add(ordinary);
                        }
                        else
                        {
                            errors.Add(line);
                        }

                        break;
                    }

                case '2' when line.Length > 1 && line[1] == ' ':
                    {
                        var renamed = ParseRenamedEntry(line);
                        if (renamed is not null)
                        {
                            entries.Add(renamed);
                        }
                        else
                        {
                            errors.Add(line);
                        }

                        break;
                    }

                case 'u' when line.Length > 1 && line[1] == ' ':
                    {
                        var unmerged = ParseUnmergedEntry(line);
                        if (unmerged is not null)
                        {
                            entries.Add(unmerged);
                        }
                        else
                        {
                            errors.Add(line);
                        }

                        break;
                    }
                case '?' when line.Length > 1 && line[1] == ' ':
                    entries.Add(ParseUntrackedEntry(line));
                    break;
                case '!':
                    break;
                default:
                    errors.Add(line);
                    break;
            }
        }

        return new RunGitStatusParseResult(branch, entries, truncated, errors);
    }

    private static bool IsHeaderLine(string line) => line.Length > 0 && (line[0] == '#' || line[0] == '!');

    private static RunGitBranchHeader ParseHeader(RunGitBranchHeader current, string line)
    {
        const string oidPrefix = "# branch.oid ";
        const string headPrefix = "# branch.head ";
        const string upstreamPrefix = "# branch.upstream ";
        const string abPrefix = "# branch.ab ";

        if (line.StartsWith(oidPrefix, StringComparison.Ordinal))
        {
            var value = line[oidPrefix.Length..];
            return current with { HeadOid = value == "(initial)" ? null : value };
        }

        if (line.StartsWith(headPrefix, StringComparison.Ordinal))
        {
            var value = line[headPrefix.Length..];
            return current with { HeadRef = value, IsDetached = value == "(detached)" };
        }

        if (line.StartsWith(upstreamPrefix, StringComparison.Ordinal))
        {
            return current with { Upstream = line[upstreamPrefix.Length..] };
        }

        if (line.StartsWith(abPrefix, StringComparison.Ordinal))
        {
            var parts = line[abPrefix.Length..].Split(' ');
            if (parts.Length == 2
                && parts[0].Length > 1
                && parts[1].Length > 1
                && long.TryParse(parts[0][1..], out var ahead)
                && long.TryParse(parts[1][1..], out var behind))
            {
                return current with { AheadCount = ahead, BehindCount = behind };
            }
        }

        return current;
    }

    private static RunGitStatusEntry? ParseOrdinaryEntry(string line)
    {
        if (!TrySplitFields(line, 8, out var fields, out var remainder))
        {
            return null;
        }

        if (!TryParseCodes(fields[1], out var indexCode, out var worktreeCode))
        {
            return null;
        }

        return new RunGitStatusEntry(
            indexCode,
            worktreeCode,
            RunGitEntryKind.Tracked,
            fields[2].StartsWith('S'),
            DecodePath(remainder),
            OriginalPath: null,
            RenameScore: null);
    }

    private static RunGitStatusEntry? ParseRenamedEntry(string line)
    {
        if (!TrySplitFields(line, 9, out var fields, out var remainder))
        {
            return null;
        }

        if (!TryParseCodes(fields[1], out var indexCode, out var worktreeCode))
        {
            return null;
        }

        var separator = remainder.IndexOf('\t');
        if (separator <= 0 || separator == remainder.Length - 1)
        {
            return null;
        }

        return new RunGitStatusEntry(
            indexCode,
            worktreeCode,
            RunGitEntryKind.Tracked,
            fields[2].StartsWith('S'),
            DecodePath(remainder[..separator]),
            DecodePath(remainder[(separator + 1)..]),
            ParseRenameScore(fields[8]));
    }

    private static RunGitStatusEntry? ParseUnmergedEntry(string line)
    {
        if (!TrySplitFields(line, 10, out var fields, out var remainder))
        {
            return null;
        }

        if (fields[1].Length != 2 || remainder.Length == 0)
        {
            return null;
        }

        return new RunGitStatusEntry(
            fields[1][..1],
            fields[1][1..],
            RunGitEntryKind.Unmerged,
            fields[2].StartsWith('S'),
            DecodePath(remainder),
            OriginalPath: null,
            RenameScore: null);
    }

    private static RunGitStatusEntry ParseUntrackedEntry(string line) =>
        new("?", "?", RunGitEntryKind.Untracked, false, DecodePath(line[2..]), null, null);

    private static bool TrySplitFields(string line, int fieldCount, out string[] fields, out string remainder)
    {
        fields = new string[fieldCount];
        var position = 0;
        for (var i = 0; i < fieldCount; i++)
        {
            var space = line.IndexOf(' ', position);
            if (space < 0)
            {
                remainder = string.Empty;
                return false;
            }

            fields[i] = line[position..space];
            position = space + 1;
        }

        remainder = line[position..];
        return true;
    }

    private static bool TryParseCodes(string xy, out string indexCode, out string worktreeCode)
    {
        indexCode = string.Empty;
        worktreeCode = string.Empty;
        if (xy.Length != 2)
        {
            return false;
        }

        indexCode = xy[..1];
        worktreeCode = xy[1..];
        return true;
    }

    private static int? ParseRenameScore(string codeWithScore)
    {
        if (codeWithScore.Length > 1 && int.TryParse(codeWithScore[1..], out var score))
        {
            return score;
        }

        return null;
    }

    internal static string DecodePath(string encoded)
    {
        if (encoded.Length >= 2 && encoded[0] == '"' && encoded[^1] == '"')
        {
            return UnescapeGitPath(encoded[1..^1]);
        }

        return encoded;
    }

    private static string UnescapeGitPath(string body)
    {
        var bytes = new List<byte>(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c != '\\' || i + 1 >= body.Length)
            {
                bytes.Add(ToByte(c));
                continue;
            }

            i++;
            switch (body[i])
            {
                case '\\':
                    bytes.Add((byte)'\\');
                    break;
                case '"':
                    bytes.Add((byte)'"');
                    break;
                case 'n':
                    bytes.Add((byte)'\n');
                    break;
                case 't':
                    bytes.Add((byte)'\t');
                    break;
                case 'r':
                    bytes.Add((byte)'\r');
                    break;
                case >= '0' and <= '7':
                    {
                        var value = 0;
                        var digits = 0;
                        while (i < body.Length && digits < 3 && body[i] is >= '0' and <= '7')
                        {
                            value = (value * 8) + (body[i] - '0');
                            digits++;
                            i++;
                        }

                        i--;
                        bytes.Add((byte)value);
                        break;
                    }
                default:
                    bytes.Add(ToByte(body[i]));
                    break;
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static byte ToByte(char c) =>
        c <= 127 ? (byte)c : (byte)'?';
}
