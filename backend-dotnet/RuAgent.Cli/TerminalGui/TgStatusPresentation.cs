using RuAgent.Cli.Tui;

namespace RuAgent.Cli.TerminalGui;

/// <summary>The actual run state owned by the hints row, independent of transient feedback.</summary>
public enum TgHintsState
{
    Idle,
    Running,
    WaitingForApproval,
    Paused,
}

/// <summary>Small, deterministic presentation rules for the Terminal.Gui status footer.</summary>
public static class TgStatusPresentation
{

    public static string StatusText(
        TgHintsState state,
        int queuedCount,
        string? feedback = null,
        bool isError = false,
        string? action = null,
        string idleLabel = "")
    {
        var queueSuffix = queuedCount > 0 ? $" · {queuedCount} queued" : string.Empty;
        if (!string.IsNullOrWhiteSpace(feedback))
        {
            var stateText = StateLabel(state, queuedCount);
            var prefix = stateText + queueSuffix;
            return (prefix.Length > 0 ? prefix + " · " : string.Empty) + (isError ? "✗ " : string.Empty) + feedback;
        }

        if (state == TgHintsState.Running && queuedCount > 0)
        {
            return "Working" + queueSuffix;
        }

        var text = state switch
        {
            TgHintsState.Running => string.IsNullOrWhiteSpace(action) ? "Working" : action,
            TgHintsState.WaitingForApproval => "▲ Approval needed",
            TgHintsState.Paused => "Ⅱ Paused",
            _ when queuedCount > 0 => "Queue waiting",
            _ => idleLabel,
        };
        return text + queueSuffix;
    }

    public static string IdleLabel(string workspace)
    {
        var safeWorkspace = ChatDisplayText.Sanitize(workspace, 200);
        var bracket = safeWorkspace.IndexOf("] ", StringComparison.Ordinal);
        return bracket > 1
            && safeWorkspace[1..bracket].Contains("attachment", StringComparison.OrdinalIgnoreCase)
                ? safeWorkspace
                : string.Empty;
    }

    private static string StateLabel(TgHintsState state, int queuedCount) => state switch
    {
        TgHintsState.Running => "Working",
        TgHintsState.WaitingForApproval => "Approval needed",
        TgHintsState.Paused => "Paused",
        _ when queuedCount > 0 => "Queue waiting",
        _ => string.Empty,
    };

    /// <summary>
    /// Keep discovery visible without turning the footer into a shortcut reference.
    /// </summary>
    public static IReadOnlyList<string> KeyHints(int width, bool compact) => ["ctrl+p commands"];
}
