using System.Globalization;
using System.Text.RegularExpressions;

namespace RuAgent.Updates;

public sealed partial record ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<ReleaseVersion>
{
    [GeneratedRegex(@"^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static ReleaseVersion? Parse(string? value)
    {
        if (value is null || value.Length > 128) return null;
        var match = Pattern().Match(value);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var patch)) return null;
        var pre = match.Groups[4].Success ? match.Groups[4].Value : null;
        if (pre?.Split('.').Any(part => part.All(char.IsAsciiDigit) && part.Length > 1 && part[0] == '0') == true) return null;
        return new(major, minor, patch, pre);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        foreach (var difference in new[] { Major.CompareTo(other.Major), Minor.CompareTo(other.Minor), Patch.CompareTo(other.Patch) })
            if (difference != 0) return difference;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            bool ln = left[i].All(char.IsAsciiDigit), rn = right[i].All(char.IsAsciiDigit);
            int difference = ln && rn ? (left[i].Length == right[i].Length
                ? string.CompareOrdinal(left[i], right[i]) : left[i].Length.CompareTo(right[i].Length))
                : ln != rn ? (ln ? -1 : 1) : string.CompareOrdinal(left[i], right[i]);
            if (difference != 0) return difference;
        }
        return left.Length.CompareTo(right.Length);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease is null ? "" : $"-{Prerelease}");
}
