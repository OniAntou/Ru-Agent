using RuAgent.Client;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private async Task ShowSubagentsAsync(string? argument, string? currentRoot, TextWriter output, bool json, CancellationToken ct)
    {
        var parts = (argument ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var root = currentRoot;
        if (parts.Length > 0 && parts[0].StartsWith("run_", StringComparison.Ordinal))
        {
            root = parts[0];
            argument = parts.Length > 1 ? parts[1] : null;
        }
        if (root is null)
        {
            await _err.WriteLineAsync("No current root. Use /subagents <run-id>.");
            return;
        }
        try { await Subagents.SubagentPresenter.ExecuteAsync(_agentClient, root, argument, output, json, ct).ConfigureAwait(false); }
        catch (AgentApiException error) { await _err.WriteLineAsync(CliErrorText.Format(error)); }
    }
}
