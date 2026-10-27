namespace RuAgent.Cli.Tui;

/// <summary>Display-only classification; never changes the workspace or execution authority.</summary>
internal static class TuiWorkspaceHeading
{
    internal const string NoProject = "Chưa chọn dự án";
    internal const string Unavailable = "Path không khả dụng";

    internal static string Describe(string path, bool explicitlySelected, string home, bool available)
    {
        if (!available || string.IsNullOrWhiteSpace(path)) return Unavailable;
        if (!explicitlySelected && !string.IsNullOrEmpty(home)
            && Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(home), StringComparison.OrdinalIgnoreCase))
            return NoProject + " · ~";
        return DisplayPath(path);
    }

    internal static string DisplayPath(string path) => TuiText.Truncate(
        TuiText.SanitizeUntrusted(path).Replace('\n', ' ').Replace('\t', ' '), 2048);
}
