using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Programs;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    private PolicyContext BindProgramContext(AgentEnvelope request, BrokerRunLease lease, PolicyContext context)
    {
        if (context.ToolId != "run_code") return context;
        if (_delegations.ContainsKey(lease.RunId)) throw new PolicyContextException("program_root_required");
        var call = PayloadReads.OptionalString(request.Payload, "tool_call_id");
        if (!SubagentContractValidator.IsValidId(call)) throw new PolicyContextException("program_invocation_invalid");
        return context with { IntentHash = ProgramRunner.Hash(context.IntentHash + "|" + call + "|" + lease.Lifetime.Id) };
    }

    private async Task<ToolResult> ExecuteProgramAsync(AgentEnvelope request, BrokerRunLease lease, CancellationToken ct)
    {
        ToolResult Refuse(string error) => new(request.RequestId, request.SessionId, ToolResultStatuses.Failed,
            new JsonObject { ["error"] = error }, error, "Program could not execute.", 0);
        if (_programJournal is null) return Refuse("program_journal_unavailable");
        if (!await _programSlots.WaitAsync(0, ct).ConfigureAwait(false)) return Refuse("program_capacity_exhausted");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var args = (JsonObject)request.Payload["arguments"]!;
        deadline.CancelAfter(args["timeout_ms"]?.GetValue<int>() ?? 30_000);
        try
        {
            var result = await new ProgramRunner(_programJournal).RunAsync(lease.RunId,
                PayloadReads.OptionalString(request.Payload, "tool_call_id")!,
                ProgramRunner.Hash(lease.SnapshotHash + "|" + lease.CanonicalRoot), args["code"]!.GetValue<string>(),
                async (call, key, token) =>
                {
                    // Recheck the outer lease too. A renewed/replaced lease may not
                    // continue a program that was authorized under another snapshot.
                    if (lease.Lifetime.IsRevoked || !TryGetOwnedRunLease(request, lease.RunId, out var current, out _)
                        || current is null || current.Lifetime != lease.Lifetime || current.BindingVersion != lease.BindingVersion)
                        throw new OperationCanceledException(token);
                    AgentEnvelope Envelope(string type, string? authorization = null) => request with
                    {
                        RequestId = "program-" + Guid.NewGuid().ToString("N"), MessageType = type,
                        Payload = new JsonObject
                        {
                            ["run_id"] = lease.RunId, ["tool_call_id"] = key, ["tool_id"] = call.ToolId,
                            ["tool_version"] = "1.0.0", ["arguments"] = call.Arguments.DeepClone(),
                            ["authorization_token"] = authorization, ["program_nested"] = true,
                        },
                    };
                    var decision = await EvaluateAsync(Envelope(AgentMessageTypes.ToolEvaluate), token).ConfigureAwait(false);
                    if (decision.Error is not null || decision.Decision?.Decision != "allow")
                        return new JsonObject { ["status"] = "denied", ["program_stop"] = true,
                            ["error_code"] = decision.Error?.ErrorCode ?? decision.Decision?.ReasonCode ?? "program_tool_denied",
                            ["output"] = new JsonObject(), ["adapter_invoked"] = false };
                    var execution = await ExecuteAuthorizedAsync(Envelope(AgentMessageTypes.ToolExecute, decision.Decision.AuthorizationToken), token).ConfigureAwait(false);
                    if (execution.Receipt is not { } receipt)
                        return new JsonObject { ["status"] = "failed", ["program_stop"] = true,
                            ["error_code"] = execution.ErrorCode ?? "program_execution_unknown", ["effects_unknown"] = true,
                            ["output"] = new JsonObject(), ["adapter_invoked"] = true };
                    var response = new JsonObject { ["status"] = receipt.Status, ["output"] = receipt.Output.DeepClone(),
                        ["error_code"] = receipt.Image is null ? receipt.ErrorCode : "program_image_requires_direct_call",
                        ["program_stop"] = receipt.Image is not null, ["adapter_invoked"] = receipt.AdapterInvoked };
                    if (System.Text.Encoding.UTF8.GetByteCount(response.ToJsonString()) > 128 * 1024)
                    {
                        var metadata = new JsonObject { ["program_output_omitted"] = true };
                        foreach (var name in new[] { "exit_code", "verification_identity", "execution_id", "state", "timed_out", "path",
                            "sha256", "before_sha256", "after_sha256", "restored_sha256", "spool_id", "recovery_id" })
                            if (receipt.Output.TryGetPropertyValue(name, out var value)) metadata[name] = value?.DeepClone();
                        response["output"] = metadata;
                        response["program_stop"] = true;
                        response["program_error"] = "program_tool_output_limit";
                    }
                    return response;
                }, deadline.Token).ConfigureAwait(false);
            var error = result["error"]?.GetValue<string>();
            return new ToolResult(request.RequestId, request.SessionId,
                error is null ? ToolResultStatuses.Succeeded : ToolResultStatuses.Failed, result, error,
                error is null ? "Program completed." : "Program stopped; inspect the recorded nested results.", 0,
                AdapterInvoked: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // A pending request is deliberately left uncertain; no automatic retry.
            var failed = Refuse("program_deadline_exceeded");
            failed.Output["effects_unknown"] = true;
            return failed with { AdapterInvoked = true };
        }
        finally { _programSlots.Release(); }
    }
}
