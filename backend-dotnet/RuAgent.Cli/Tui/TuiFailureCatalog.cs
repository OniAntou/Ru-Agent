using RuAgent.Client;

namespace RuAgent.Cli.Tui;

/// <summary>One cataloged failure: title, cause and fix for a bounded error-code family.</summary>
public sealed record TuiFailureInfo(string Code, string Title, string Cause, string Fix);

/// <summary>
/// The terminal surface over the shared failure copy table. Every bounded error
/// code the Host can end a turn with maps to a title, a cause and an actionable
/// fix; a code outside the table still gets a human sentence and a pointer at the
/// logs, never a bare machine token in the transcript. The copy itself lives in
/// <see cref="AgentFailureCopy"/> so the terminal and the desktop client resolve
/// the same wording from one source; the raw code stays available to diagnostics
/// and to <see cref="TuiDebug"/>.
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
            if (TryGet(key, out info))
            {
                return info;
            }
        }

        return new TuiFailureInfo(
            key,
            "Run stopped",
            "something interrupted the turn before it could finish",
            @"send a new message to retry · check the logs under %LOCALAPPDATA%\Ru\logs for details");
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
