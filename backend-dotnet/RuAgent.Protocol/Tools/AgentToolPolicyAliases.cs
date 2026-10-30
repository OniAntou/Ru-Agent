namespace RuAgent.Protocol.Tools;

/// <summary>
/// One legacy rule tool-id name and the call it covers. A rule written against <see cref="RuleToolId"/> matches a
/// call whose policy family is <see cref="Family"/> when the call's operation is <see cref="ToolOp"/> and its bash
/// mode is <see cref="BashMode"/>. A null condition matches any value.
/// </summary>
public sealed record AgentToolPolicyAlias(string RuleToolId, string Family, string? ToolOp = null, string? BashMode = null);

/// <summary>
/// The single source for legacy rule tool-id names (<c>files.*</c>, <c>terminal.*</c>, <c>web.*</c>,
/// <c>help.docs</c>) and the family, operation or bash mode each one covers. Advertised tool ids are matched through
/// <see cref="AgentToolDescriptorTable"/>; this table only carries the older names that rules may still use.
/// </summary>
public static class AgentToolPolicyAliases
{
    private static readonly AgentToolPolicyAlias[] Rows =
    [
        new("files.list", "read", ToolOp: "list"),
        new("files.glob", "glob"),
        new("files.glob", "read", ToolOp: "list"),
        new("files.search", "grep"),
        new("files.search", "read", ToolOp: "list"),
        new("files.read", "read", ToolOp: "read"),
        new("files.write", "write", ToolOp: "write"),
        new("files.edit", "edit"),
        new("files.delete", "write", ToolOp: "delete"),
        new("files.restore", "write", ToolOp: "restore"),
        new("terminal.run", "bash", BashMode: "run"),
        new("terminal.shell", "bash", BashMode: "shell"),
        new("web.search", "read", ToolOp: "web_search"),
        new("web.fetch", "read", ToolOp: "web_fetch"),
        new("web.search", "web_search"),
        new("web.fetch", "web_fetch"),
        new("help.docs", "read", ToolOp: "help"),
    ];

    /// <summary>Every legacy alias row, in declaration order.</summary>
    public static IReadOnlyList<AgentToolPolicyAlias> All { get; } = Array.AsReadOnly(Rows);

    /// <summary>
    /// Whether a rule that names <paramref name="ruleToolId"/> covers a call of <paramref name="family"/> under the
    /// given operation and bash mode. False when no alias row names that pair.
    /// </summary>
    public static bool Matches(string ruleToolId, string family, string? toolOp, string? bashMode)
    {
        foreach (var alias in Rows)
        {
            if (string.Equals(alias.RuleToolId, ruleToolId, StringComparison.Ordinal)
                && string.Equals(alias.Family, family, StringComparison.Ordinal)
                && (alias.ToolOp is null || string.Equals(alias.ToolOp, toolOp, StringComparison.Ordinal))
                && (alias.BashMode is null || string.Equals(alias.BashMode, bashMode, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
