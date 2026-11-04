using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

/// <summary>
/// Words for activity facts the transcript shows. Status wording follows the Desktop
/// feed (<c>AgentActivityItem.StatusText</c>) so both surfaces describe a run alike.
/// </summary>
internal static class TuiActivityLabels
{
    /// <summary>
    /// A completed status row that only says the step went fine explains nothing; the
    /// Desktop feed hides it too. A failed one always shows.
    /// </summary>
    internal static bool IsSilentStatus(AgentActivityState state, string safeCode) =>
        state != AgentActivityState.Failed && safeCode is "ok" or "succeeded" or "completed";

    internal static string StatusText(string safeCode) => safeCode switch
    {
        "frames_dropped" => "some steps were temporarily skipped",
        "subagent_spawned" => "started a subagent",
        "subagent_wait_timeout" => "checked subagents · still working",
        "subagent_wait_completed" => "received subagent results",
        "inspection_required" => "waiting: an action needs a read-only check first",
        "reconciliation_required" => "stopped: the last change is unverified",
        { Length: > 0 } code => "status: " + code.Replace('_', ' '),
        _ => "status update",
    };

    /// <summary>
    /// The statement a command row names. Scripts often open with preference or location
    /// setup (<c>$ErrorActionPreference='Stop'; Set-Location …</c>), and a row that shows
    /// that, or cuts the middle out of a long script, says nothing about what ran. The
    /// row names the first real statement and counts the rest.
    /// </summary>
    internal static (string Head, int More) CommandHead(string command)
    {
        var statements = SplitStatements(command);
        if (statements.Count == 0) return (command.Trim(), 0);
        var index = statements.FindIndex(statement => !IsPrelude(statement));
        if (index < 0) index = 0;
        return (statements[index], statements.Count - index - 1);
    }

    private static bool IsPrelude(string statement)
    {
        var text = statement.TrimStart();
        return text.StartsWith('$') && PreferenceNames.Any(name =>
                   text.StartsWith("$" + name, StringComparison.OrdinalIgnoreCase)
                   && text[(name.Length + 1)..].TrimStart().StartsWith('='))
            || IsEnvironmentAssignment(text)
            || text.StartsWith("[Net.ServicePointManager]::", StringComparison.OrdinalIgnoreCase)
            || IsAssignment(text, "[Console]::OutputEncoding")
            || IsAssignment(text, "[Console]::InputEncoding")
            || text.StartsWith("Set-StrictMode", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Set-Location", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Push-Location", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("cd ", StringComparison.OrdinalIgnoreCase);
    }

    // Setting the console encoding is setup; reading it ("[Console]::OutputEncoding.WebName") is the work.
    private static bool IsAssignment(string text, string target) =>
        text.StartsWith(target, StringComparison.OrdinalIgnoreCase) && text[target.Length..].TrimStart().StartsWith('=');

    private static bool IsEnvironmentAssignment(string text)
    {
        if (!text.StartsWith("$env:", StringComparison.OrdinalIgnoreCase)) return false;
        var equals = text.IndexOf('=');
        return equals > 5 && text[5..equals].Trim().All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    private static readonly string[] PreferenceNames =
        ["ErrorActionPreference", "ProgressPreference", "WarningPreference", "VerbosePreference",
         "InformationPreference", "OutputEncoding", "PSNativeCommandUseErrorActionPreference"];

    /// <summary>Splits on newlines and semicolons outside quotes; display only, never execution.</summary>
    private static List<string> SplitStatements(string command)
    {
        var statements = new List<string>();
        var current = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (var ch in command)
        {
            if (quote != '\0')
            {
                current.Append(ch);
                if (ch == quote) quote = '\0';
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                current.Append(ch);
                continue;
            }
            if (ch is ';' or '\n' or '\r')
            {
                Commit();
                continue;
            }
            current.Append(ch);
        }
        Commit();
        return statements;

        void Commit()
        {
            var text = current.ToString().Trim();
            if (text.Length > 0) statements.Add(text);
            current.Clear();
        }
    }
}
