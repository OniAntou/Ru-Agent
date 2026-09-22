using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private async ValueTask<ToolExecutionOutcome> ExecuteBashAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var mode = ReadOptionalString(arguments, "mode") ?? "run";
        if (string.Equals(mode, "shell", StringComparison.Ordinal))
        {
            return await ExecuteTerminalShellAsync(context, arguments, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteTerminalRunAsync(context, arguments, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteTerminalRunAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var workingDirectory = ReadOptionalString(arguments, "working_directory") ?? ".";
        var tools = ResolveRootToolSet(context, workingDirectory);
        var grant = new PolicyGrant(
            context.SessionId,
            context.SessionScopes ?? _session.Scopes,
            _session.Descriptor.ExpiresAt);
        var result = await tools.Terminal.RunAsync(
            grant,
            new TerminalRunRequest(
                ReadOptionalString(arguments, "shell") ?? "powershell",
                workingDirectory,
                ReadString(arguments, "executable"),
                ReadArguments(arguments["arguments"]),
                ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
                IsTrue(arguments, "network_intent") || IsTrue(arguments, "network"),
                context.ApprovalToken ?? string.Empty,
                context.RequestId,
                context.SessionId,
                IsTrue(arguments, "read_only"),
                ReadWriteSet(arguments),
                Approved: context.Approved),
            context.ConfinementRequired,
            cancellationToken).ConfigureAwait(false);
        return ProcessOutcome(result, context.ConfinementRequired, tools.CanonicalRoot);
    }

    private async ValueTask<ToolExecutionOutcome> ExecuteTerminalShellAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        // Shell scripts always require explicit approval: the evaluator asks with
        // terminal_unknown_executable and the execution leg consumes the same
        // one-time token. Confinement is defense in depth, not the gate.
        if (!context.Approved && context.ApprovalToken is null)
        {
            throw new UnauthorizedAccessException("terminal_shell_approval_invalid");
        }

        var workingDirectory = ReadOptionalString(arguments, "working_directory") ?? ".";
        var tools = ResolveRootToolSet(context, workingDirectory);
        var shellRequest = new TerminalShellRequest(
            ReadOptionalString(arguments, "shell") ?? "powershell",
            workingDirectory,
            ReadString(arguments, "script"),
            ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
            context.ApprovalToken ?? string.Empty,
            context.RequestId,
            context.SessionId,
            IsTrue(arguments, "read_only"),
            ReadWriteSet(arguments),
            Approved: context.Approved);
        var result = await tools.Terminal.RunApprovedShellAsync(
            shellRequest,
            context.ConfinementRequired,
            cancellationToken).ConfigureAwait(false);
        return ProcessOutcome(result, context.ConfinementRequired, tools.CanonicalRoot);
    }

    /// <summary>
    /// Resolves the canonical intent hash for a terminal call without executing it.
    /// Approval tokens bind to this hash so any change to the resolved executable,
    /// argv, working directory, operation class, network intent, or timeout between
    /// approval and execution invalidates the authorization.
    /// </summary>
    private string? TryComputeTerminalIntentHash(string toolId, JsonObject arguments) =>
        TryComputeTerminalIntentHash(_defaultTools, toolId, arguments);

    private string? TryComputeTerminalIntentHash(RootToolSet tools, string toolId, JsonObject arguments)
    {
        try
        {
            var mode = ReadOptionalString(arguments, "mode")
                ?? (toolId == "bash" ? "run" : null);
            if (string.Equals(mode, "shell", StringComparison.Ordinal) || toolId == "terminal.shell")
            {
                return tools.Terminal.CanonicalizeShell(new TerminalShellRequest(
                    ReadOptionalString(arguments, "shell") ?? "powershell",
                    ReadOptionalString(arguments, "working_directory") ?? ".",
                    ReadString(arguments, "script"),
                    ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
                    string.Empty)).IntentHash;
            }

            return toolId is "terminal.run" or "bash"
                ? tools.Terminal.CanonicalizeRun(new TerminalRunRequest(
                    "powershell",
                    ReadOptionalString(arguments, "working_directory") ?? ".",
                    ReadString(arguments, "executable"),
                    ReadArguments(arguments["arguments"]),
                    ReadOptionalInt(arguments, "timeout_ms") ?? TerminalLimits.DefaultTimeoutMs,
                    IsTrue(arguments, "network_intent") || IsTrue(arguments, "network"))).IntentHash
                : null;
        }
        catch (Exception error) when (
            error is ArgumentException or UnauthorizedAccessException or KeyNotFoundException
                or ToolExecutionException)
        {
            return null;
        }
    }

    private static string TerminalRunPreview(JsonObject arguments)
    {
        var executable = ReadOptionalString(arguments, "executable");
        if (string.IsNullOrWhiteSpace(executable))
        {
            return string.Empty;
        }

        var parts = new List<string> { executable };
        if (arguments["arguments"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is { } node && node.GetValueKind() == System.Text.Json.JsonValueKind.String)
                {
                    parts.Add(node.GetValue<string>());
                }
            }
        }

        return string.Join(' ', parts);
    }
}
