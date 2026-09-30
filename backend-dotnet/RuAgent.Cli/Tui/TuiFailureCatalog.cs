using RuAgent.Client;

namespace RuAgent.Cli.Tui;

/// <summary>One cataloged failure: title, cause and fix for a bounded error-code family.</summary>
public sealed record TuiFailureInfo(string Code, string Title, string Cause, string Fix);

/// <summary>
/// The terminal surface over the shared failure copy table. Every bounded error
/// code the Host can end a turn with maps to a title, a cause and an actionable
/// fix; codes outside the table still name themselves and point at the logs
/// instead of rendering a bare token. The copy itself lives in
/// <see cref="AgentFailureCopy"/> so the terminal and the desktop client resolve
/// the same wording from one source.
/// </summary>
public static class TuiFailureCatalog
{
    public static TuiFailureInfo For(string? code)
    {
        var key = (code ?? string.Empty).Trim();
        if (TryGet(key, out var info))
        {
            return info;
        }

        if (key.Length == 0)
        {
            key = "run_failed";
        }

        return new TuiFailureInfo(
            key,
            "Error",
            $"unrecognized error '{key}'",
            @"check the logs under %LOCALAPPDATA%\Ru\logs and run /status for details");
    }

    public static bool TryGet(string? code, out TuiFailureInfo info)
    {
        var key = (code ?? string.Empty).Trim();
        if (AgentFailureCopy.TryGet(key, out var copy))
        {
            info = new TuiFailureInfo(key, copy.Title, copy.Cause, copy.Fix);
            return true;
        }

        info = null!;
        return false;
    }
}
