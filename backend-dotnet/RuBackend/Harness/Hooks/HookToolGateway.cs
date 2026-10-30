using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;

namespace RuBackend.Harness.Hooks;

/// <summary>
/// Runs user hooks around an <see cref="IToolGateway"/>. A PreToolUse hook that exits
/// with code 2 rejects the call before policy evaluation, so no approval is asked; its
/// stderr becomes the model-visible deny hint. Any other non-zero exit, a timeout or a
/// start failure never blocks. PostToolUse hooks observe the receipt and cannot change it.
/// </summary>
public sealed class HookToolGateway : IToolGateway, IToolRunCancellation, IApprovalCleanupGateway
{
    public const string BlockedCode = "hook_blocked";
    private const int MaxTrackedCalls = 4096;
    private const int MaxArgumentChars = 32 * 1024;

    private readonly IToolGateway _inner;
    private readonly HookConfiguration _hooks;
    private readonly IHookRunner _runner;
    private readonly string _workspaceRoot;
    private readonly ConcurrentDictionary<string, ToolProposal> _evaluated = new(StringComparer.Ordinal);

    public HookToolGateway(IToolGateway inner, HookConfiguration hooks, IHookRunner runner, string workspaceRoot)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _workspaceRoot = workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot));
    }

    public async Task<ToolProposalDecision> EvaluateAsync(ToolProposal proposal, CancellationToken cancellationToken)
    {
        foreach (var hook in _hooks.For(HookEvent.PreToolUse, proposal.ToolId))
        {
            var result = await TryRunAsync(hook, EventJson(hook.Event, proposal, outcome: null), cancellationToken).ConfigureAwait(false);
            if (result is { Blocks: true })
            {
                var message = string.IsNullOrWhiteSpace(result.Message) ? "blocked by a PreToolUse hook" : result.Message;
                return ToolProposalDecision.Deny(AccessKindOf(proposal), BlockedCode, "hook: " + message);
            }
        }

        var decision = await _inner.EvaluateAsync(proposal, cancellationToken).ConfigureAwait(false);
        if (decision.Decision != RuAgent.Harness.Core.Policy.PolicyDecision.Deny && _hooks.Has(HookEvent.PostToolUse))
        {
            if (_evaluated.Count >= MaxTrackedCalls) _evaluated.Clear();
            _evaluated[proposal.ToolCallId.Value] = proposal;
        }

        return decision;
    }

    public Task<PreparedToolExecution> PrepareAsync(ApprovedToolExecution execution, CancellationToken cancellationToken) =>
        _inner.PrepareAsync(execution, cancellationToken);

    public async Task<ToolExecutionReceipt> ExecuteAsync(
        PreparedToolExecution execution,
        Func<ToolOutputChunk, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        var receipt = await _inner.ExecuteAsync(execution, publishDelta, cancellationToken).ConfigureAwait(false);
        if (_evaluated.TryRemove(receipt.ToolCallId.Value, out var proposal))
        {
            foreach (var hook in _hooks.For(HookEvent.PostToolUse, proposal.ToolId))
            {
                await TryRunAsync(hook, EventJson(hook.Event, proposal, receipt), CancellationToken.None).ConfigureAwait(false);
            }
        }

        return receipt;
    }

    public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken) =>
        _inner.CancelAsync(executionId, cancellationToken);

    public Task CancelRunAsync(RunId runId, CancellationToken cancellationToken) =>
        _inner is IToolRunCancellation cancellation ? cancellation.CancelRunAsync(runId, cancellationToken) : Task.CompletedTask;

    public Task<bool> RevokeApprovalAsync(ApprovalId approvalId, CancellationToken cancellationToken) =>
        _inner is IApprovalCleanupGateway cleanup ? cleanup.RevokeApprovalAsync(approvalId, cancellationToken) : Task.FromResult(false);

    private async Task<HookResult?> TryRunAsync(HookDefinition hook, string json, CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(hook, json, _workspaceRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private string EventJson(HookEvent hookEvent, ToolProposal proposal, ToolExecutionReceipt? outcome)
    {
        var raw = proposal.Arguments.GetRawText();
        var truncated = raw.Length > MaxArgumentChars;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("event", hookEvent.ToString());
            writer.WriteString("tool_id", proposal.ToolId);
            writer.WriteString("tool_call_id", proposal.ToolCallId.Value);
            writer.WriteString("run_id", proposal.RunId.Value);
            writer.WriteString("workspace", _workspaceRoot);
            writer.WriteBoolean("arguments_truncated", truncated);
            if (!truncated)
            {
                writer.WritePropertyName("arguments");
                proposal.Arguments.WriteTo(writer);
            }

            if (outcome is not null)
            {
                writer.WriteString("outcome", outcome.Outcome.ToString());
                writer.WriteString("code", outcome.Code);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    // The same classification the Broker gateway uses (ToolAccessClassification), so a denied call
    // reports the access class it would have had if it had reached the authority.
    private static ToolAccessKind AccessKindOf(ToolProposal proposal) =>
        ToolAccessClassification.Classify(proposal.ToolId, ArgumentsObjectOf(proposal));

    private static JsonObject? ArgumentsObjectOf(ToolProposal proposal) =>
        proposal.Arguments.ValueKind is JsonValueKind.Object
            ? JsonNode.Parse(proposal.Arguments.GetRawText()) as JsonObject
            : null;
}
