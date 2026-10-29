namespace RuAgent.Cli.Tui;

/// <summary>Display-only path sanitization; never changes the workspace or execution authority.</summary>
internal static class TuiWorkspaceHeading
{
    internal static string DisplayPath(string path) => TuiText.Truncate(
        TuiText.SanitizeUntrusted(path).Replace('\n', ' ').Replace('\t', ' '), 2048);
}
