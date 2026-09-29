using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    public async Task<int> PolicyListAsync(PolicyListCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            if (cmd.WorkspacePath is not null)
            {
                // The client has no path -> workspace_id resolution, and sending the
                // path as an id would make the server filter on a key that cannot
                // match. Fail clearly instead of silently listing the wrong scope.
                await _err.WriteLineAsync(
                    "workspace_scope_unsupported: 'agent policy list' cannot resolve --workspace PATH; omit --workspace or use --global.")
                    .ConfigureAwait(false);
                return 1;
            }

            string? scope = cmd.Global ? "global" : null;
            var items = await _agentClient.ListPoliciesAsync(scope, null, cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                var obj = new { v = 1, type = "policy_list", items = items.Select(e => JsonSerializer.Deserialize<object>(e.GetRawText())).ToList() };
                await _out.WriteLineAsync(JsonSerializer.Serialize(obj)).ConfigureAwait(false);
            }
            else
            {
                await _out.WriteLineAsync($"policies: {items.Count}").ConfigureAwait(false);
                foreach (var el in items)
                {
                    await _out.WriteLineAsync(el.GetRawText()).ConfigureAwait(false);
                }
            }

            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
    }

    public async Task<int> PolicyImportAsync(PolicyImportCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd.Json)
        {
            await _err.WriteLineAsync("interactive_required: --json cannot import global policy").ConfigureAwait(false);
            return 1;
        }

        PolicyFile file;
        try
        {
            file = PolicyJsonCodec.LoadFromFile(cmd.FilePath);
        }
        catch (PolicyJsonException ex)
        {
            await _err.WriteLineAsync($"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"file_error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        var display = PolicyJsonCodec.NormalizeAndDisplay(file);
        await _out.WriteLineAsync("Normalized policy to import:").ConfigureAwait(false);
        await _out.WriteLineAsync(display).ConfigureAwait(false);
        await _out.WriteAsync("Confirm import (y/N): ").ConfigureAwait(false);
        await _out.FlushAsync().ConfigureAwait(false);
        var answer = await _in.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var normalized = answer?.Trim().ToLowerInvariant();
        if (normalized != "y" && normalized != "yes")
        {
            await _err.WriteLineAsync("cancelled: import not confirmed.").ConfigureAwait(false);
            return 1;
        }

        foreach (var rule in file.Rules)
        {
            var matcher = new
            {
                tool_id = rule.Matcher.ToolId,
                tool_version = rule.Matcher.ToolVersion,
                canonical_path_pattern = rule.Matcher.CanonicalPathPattern,
                resolved_executable = rule.Matcher.ResolvedExecutable,
                argv_pattern = rule.Matcher.ArgvPattern,
                cwd_pattern = rule.Matcher.CwdPattern,
                operation_class = rule.Matcher.OperationClass,
                network_intent = rule.Matcher.NetworkIntent,
            };
            var body = new
            {
                scope = rule.Scope,
                decision = rule.Decision,
                matcher,
            };

            try
            {
                var result = await _agentClient.CreatePolicyAsync(body, interactive: true, cancellationToken).ConfigureAwait(false);
                await _out.WriteLineAsync($"imported rule {result.GetProperty("rule_id").GetString()} scope:{rule.Scope}").ConfigureAwait(false);
            }
            catch (AgentApiException ex)
            {
                await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
                return 1;
            }
        }

        return 0;
    }

    public async Task<int> PolicyRemoveAsync(PolicyRemoveCommand cmd, CancellationToken cancellationToken)
    {
        if (cmd.Json)
        {
            await _err.WriteLineAsync("interactive_required: --json cannot remove policy").ConfigureAwait(false);
            return 1;
        }

        await _out.WriteAsync($"Confirm removal of {cmd.RuleId} (y/N): ").ConfigureAwait(false);
        await _out.FlushAsync().ConfigureAwait(false);
        var answer = await _in.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var normalized = answer?.Trim().ToLowerInvariant();
        if (normalized != "y" && normalized != "yes")
        {
            await _err.WriteLineAsync("cancelled: removal not confirmed.").ConfigureAwait(false);
            return 1;
        }

        try
        {
            await _agentClient.DeletePolicyAsync(cmd.RuleId, interactive: true, cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync($"removed {cmd.RuleId}").ConfigureAwait(false);
            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
    }
}
