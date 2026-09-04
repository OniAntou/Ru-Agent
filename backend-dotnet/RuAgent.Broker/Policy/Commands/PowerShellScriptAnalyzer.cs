using System.Management.Automation.Language;
using RuAgent.Protocol;

namespace RuAgent.Broker.Policy.Commands;

/// <summary>
/// Classifies a complete PowerShell script without executing it. Every command must be
/// statically identifiable; one delete or unknown invocation gates the entire script.
/// </summary>
public sealed class PowerShellScriptAnalyzer
{
    private static readonly HashSet<string> DeleteCommands = new(
        ["Remove-Item", "ri", "rm", "del", "erase", "rmdir", "rd"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DynamicCommands = new(
        ["Invoke-Expression", "iex"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> KnownAliases = new(
        [
            "cd", "chdir", "cls", "clear", "copy", "cp", "dir", "echo", "foreach",
            "ft", "fl", "fw", "gal", "gc", "gci", "gcm", "gdr", "gi", "gl", "gm",
            "gp", "gps", "group", "gv", "h", "history", "join", "ls", "measure",
            "move", "mv", "ni", "pwd", "ren", "rename", "select", "sleep", "sort",
            "tee", "type", "where", "write",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly CommandAnalyzerRegistry _commands;

    public PowerShellScriptAnalyzer(CommandAnalyzerRegistry commands)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    public CommandAnalysis Analyze(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return Unknown("powershell_script_empty");
        }

        var ast = Parser.ParseInput(script, out _, out var errors);
        if (errors.Length != 0)
        {
            return Unknown("powershell_parse_error");
        }

        var analyses = ast
            .FindAll(static node => node is CommandAst, searchNestedScriptBlocks: true)
            .Cast<CommandAst>()
            .Select(AnalyzeCommand)
            .ToArray();
        if (analyses.Length == 0)
        {
            return Unknown("powershell_command_missing");
        }

        var deletion = analyses.FirstOrDefault(item => item.OperationClass == TerminalOperationClass.DirectDelete);
        if (deletion is not null)
        {
            return deletion;
        }

        var unknown = analyses.FirstOrDefault(item => item.OperationClass == TerminalOperationClass.Unknown);
        return unknown ?? new CommandAnalysis(
            CommandAnalyzerRegistry.Version,
            TerminalOperationClass.ReadOnly,
            "none",
            "powershell_recognized_non_delete");
    }

    private CommandAnalysis AnalyzeCommand(CommandAst command)
    {
        var name = command.GetCommandName();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Unknown("powershell_dynamic_invocation");
        }

        if (DeleteCommands.Contains(name))
        {
            return new CommandAnalysis(
                CommandAnalyzerRegistry.Version,
                TerminalOperationClass.DirectDelete,
                "none",
                "terminal_direct_delete");
        }

        if (DynamicCommands.Contains(name))
        {
            return Unknown("powershell_dynamic_invocation");
        }

        if (IsKnownPowerShellCommand(name))
        {
            return new CommandAnalysis(
                CommandAnalyzerRegistry.Version,
                TerminalOperationClass.ReadOnly,
                "none",
                "powershell_static_command");
        }

        var arguments = command.CommandElements
            .Skip(1)
            .Select(ElementText)
            .ToArray();
        return _commands.Analyze(name, arguments);
    }

    private static bool IsKnownPowerShellCommand(string name) =>
        KnownAliases.Contains(name)
        || (name.Contains('-', StringComparison.Ordinal)
            && Path.GetExtension(name).Length == 0
            && !name.Contains('/')
            && !name.Contains('\\'));

    private static string ElementText(CommandElementAst element) => element switch
    {
        StringConstantExpressionAst literal => literal.Value,
        ExpandableStringExpressionAst expandable when expandable.NestedExpressions.Count == 0 => expandable.Value,
        _ => element.Extent.Text,
    };

    private static CommandAnalysis Unknown(string reasonCode) => new(
        CommandAnalyzerRegistry.Version,
        TerminalOperationClass.Unknown,
        "none",
        reasonCode);
}
