using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.Tui;

/// <summary>
/// The verb a settled tool row and the live row use for one activity kind or provider
/// display name. Providers name their tools (<c>View</c>, <c>Terminal</c>, <c>Edit</c>);
/// an operator reads those rows as a sentence, so a noun is turned into a verb
/// (<c>read</c>, <c>ran</c>, <c>edited</c>) and anything unrecognised keeps its own name
/// rather than being invented.
/// </summary>
public static class TuiToolVerbs
{
    private static readonly Dictionary<string, (string Present, string Past)> ByName = new(StringComparer.Ordinal)
    {
        ["read"] = ("reading", "read"),
        ["read_file"] = ("reading", "read"),
        ["write_file"] = ("writing", "wrote"),
        ["edit_file"] = ("editing", "edited"),
        ["apply_patch"] = ("editing", "edited"),
        ["view"] = ("reading", "read"),
        ["cat"] = ("reading", "read"),
        ["open"] = ("reading", "read"),
        ["list"] = ("listing", "listed"),
        ["ls"] = ("listing", "listed"),
        ["glob"] = ("listing", "listed"),
        ["explore"] = ("exploring", "explored"),
        ["grep"] = ("searching", "searched"),
        ["search"] = ("searching", "searched"),
        ["find"] = ("searching", "searched"),
        ["write"] = ("writing", "wrote"),
        ["create"] = ("writing", "wrote"),
        ["edit"] = ("editing", "edited"),
        ["patch"] = ("editing", "edited"),
        ["replace"] = ("editing", "edited"),
        ["delete"] = ("deleting", "deleted"),
        ["remove"] = ("deleting", "deleted"),
        ["restore"] = ("restoring", "restored"),
        ["bash"] = ("running", "ran"),
        ["terminal"] = ("running", "ran"),
        ["shell"] = ("running", "ran"),
        ["run"] = ("running", "ran"),
        ["exec"] = ("running", "ran"),
        ["todo"] = ("updating the plan", "planned"),
        ["plan"] = ("updating the plan", "planned"),
        ["web_search"] = ("searching the web", "searched the web"),
        ["web search"] = ("searching the web", "searched the web"),
        ["output"] = ("reading output", "read output"),
        ["stop"] = ("stopping", "stopped"),
        ["docs"] = ("checking the docs", "checked the docs"),
        ["mcp"] = ("calling", "called"),
        ["computer"] = ("using the computer", "used the computer"),
        ["program"] = ("running a program", "ran a program"),
        ["web_fetch"] = ("fetching", "fetched"),
        ["fetch"] = ("fetching", "fetched"),
        ["skill"] = ("loading", "loaded"),
        ["help"] = ("checking the docs", "checked the docs"),
        ["analyze"] = ("analyzing", "analyzed"),
        ["analyse"] = ("analyzing", "analyzed"),
        ["think"] = ("thinking", "thought"),
    };

    private static readonly Dictionary<AgentActivityKind, (string Present, string Past)> ByKind = new()
    {
        [AgentActivityKind.Explore] = ("reading", "read"),
        [AgentActivityKind.Search] = ("searching", "searched"),
        [AgentActivityKind.Analyze] = ("analyzing", "analyzed"),
        [AgentActivityKind.Edit] = ("editing", "edited"),
        [AgentActivityKind.Terminal] = ("running", "ran"),
        [AgentActivityKind.Thinking] = ("thinking", "thought"),
        [AgentActivityKind.Todo] = ("updating the plan", "planned"),
        [AgentActivityKind.Compaction] = ("compacting", "compacted"),
        [AgentActivityKind.Retry] = ("retrying", "retried"),
        [AgentActivityKind.Web] = ("browsing", "browsed"),
        [AgentActivityKind.Mcp] = ("calling", "called"),
        [AgentActivityKind.Computer] = ("using the computer", "used the computer"),
        [AgentActivityKind.Skill] = ("loading", "loaded"),
        [AgentActivityKind.Program] = ("running a program", "ran a program"),
    };

    /// <summary>Present-tense verb for the live row, e.g. <c>reading app.cs</c>.</summary>
    public static string Present(AgentActivityKind kind, string? toolDisplay)
    {
        var name = toolDisplay?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(name) && ByName.TryGetValue(name, out var byName))
        {
            return byName.Present;
        }

        return ByKind.TryGetValue(kind, out var byKind) ? byKind.Present : Fallback(name);
    }

    /// <summary>Past-tense verb for the settled row, e.g. <c>read app.cs</c>.</summary>
    public static string Past(AgentActivityKind kind, string? toolDisplay)
    {
        var name = toolDisplay?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(name) && ByName.TryGetValue(name, out var byName))
        {
            return byName.Past;
        }

        if (ByKind.TryGetValue(kind, out var byKind))
        {
            return byKind.Past;
        }

        return Fallback(name);
    }

    private static string Fallback(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "tool" : name.Trim().ToLowerInvariant();
}
