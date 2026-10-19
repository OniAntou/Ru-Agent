using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness.Broker;

/// <summary>
/// The mapping rules the in-process <see cref="IToolGateway"/> implementation
/// uses to project the execution authority's typed results into model-visible
/// decisions and receipts. Run-open payload construction lives here too so
/// lease building stays in one place.
/// </summary>
internal static class BrokerGatewayContracts
{
    internal const string AuthorizationTokenPrefix = "auth:";
    internal const int MaximumRepairHintLength = 512;
    private const int MaximumFailureDiagnosticLength = 2048;



    /// <summary>The run lease lifetime every gateway-issued lease carries.</summary>
    internal static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Conservative access classification for a tool call: everything not known
    /// to be read-only is treated as terminal (the widest side-effect class).
    /// </summary>
    internal static ToolAccessKind AccessKindForTool(string toolId, JsonObject? arguments = null) =>
        toolId switch
        {
            "write" or "edit" or "todo_write" or "agent_spawn" or "agent_send" or "agent_cancel" or "agent_apply" => ToolAccessKind.Mutation,
            "agent_wait" => ToolAccessKind.ReadOnly,
            "bash" => ToolAccessKind.Terminal,
            _ when BrokerPolicyContracts.IsReadOnlyTool(toolId) => ToolAccessKind.ReadOnly,
            _ => ToolAccessKind.Terminal,
        };

    internal static string ApprovalArgsKey(string toolId, JsonElement arguments)
    {
        var canonicalArguments = CanonicalizeJson(JsonNode.Parse(arguments.GetRawText()));
        return toolId + "|" + (canonicalArguments?.ToJsonString() ?? "null");
    }

    internal static string ProposalApprovalArgsKey(ToolProposal proposal) =>
        ApprovalArgsKey(proposal.ToolId, proposal.Arguments)
        + (RuAgent.Protocol.Subagents.SubagentContractValidator.IsControlTool(proposal.ToolId) ? "|call:" + proposal.ToolCallId.Value : string.Empty);

    private static JsonNode? CanonicalizeJson(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(
            obj.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => KeyValuePair.Create<string, JsonNode?>(item.Key, CanonicalizeJson(item.Value)))),
        JsonArray array => new JsonArray(array.Select(item => (JsonNode?)CanonicalizeJson(item)).ToArray()),
        _ => node.DeepClone(),
    };

    /// <summary>
    /// The Host-facing approval id derived from the authority's opaque id. The
    /// derivation is one-way: the model and the Host only ever see the digest.
    /// </summary>
    internal static ApprovalId InternalApprovalId(string brokerApprovalId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(brokerApprovalId));
        return new ApprovalId("apr_" + Convert.ToHexString(digest, 0, 16).ToLowerInvariant());
    }

    /// <summary>
    /// The bounded reason codes a Host ask may carry. Anything else collapses to
    /// the generic approval_required so the model never sees an unbounded code.
    /// </summary>
    internal static string MapAskReasonCode(string? reasonCode) => reasonCode switch
    {
        "external_directory_required" => "external_directory_required",
        "terminal_external_cwd_requires_approval" => "terminal_external_cwd_requires_approval",
        "rule_asks" => "rule_asks",
        "terminal_delete_requires_approval" => "terminal_delete_requires_approval",
        "terminal_external_mutation_requires_approval" => "terminal_external_mutation_requires_approval",
        "terminal_timeout_requires_approval" => "terminal_timeout_requires_approval",
        "terminal_unknown_executable" => "terminal_unknown_executable",
        "terminal_protected_path_requires_approval" => "terminal_protected_path_requires_approval",
        "web_approval_required" => "web_approval_required",
        "subagent_apply_approval_required" => "subagent_apply_approval_required",
        _ => "approval_required",
    };

    internal static ToolExecutionReceipt SucceededReceipt(
        PreparedToolExecution execution,
        JsonObject output,
        ToolAccessKind accessKind)
    {
        return new ToolExecutionReceipt(
            execution.ExecutionId,
            execution.ToolCallId,
            ToolExecutionOutcome.Succeeded,
            "ok",
            null,
            sideEffectsPossible: accessKind != ToolAccessKind.ReadOnly,
            modelContent: Encoding.UTF8.GetBytes(output.ToJsonString()),
            contentEncoding: HarnessPayloadEncoding.Json);
    }

    internal static ToolExecutionReceipt FailedReceipt(
        PreparedToolExecution execution,
        string code,
        string? safeSummary = null,
        JsonObject? output = null)
    {
        var content = new JsonObject { ["error"] = code };
        if (BoundRepairHint(safeSummary) is { } hint)
        {
            content["hint"] = hint;
        }

        if (BoundSpoolId(output) is { } spoolId)
        {
            content["spool_id"] = spoolId;
        }

        // Terminal output is redacted by the execution authority. Preserve just
        // enough of it to repair a failed build without another tool round trip.
        if (output is not null && code is "process_exit_nonzero" or "process_start_failed" or "process_timed_out")
        {
            if (output["exit_code"] is JsonValue exit && exit.TryGetValue<int>(out var exitCode))
            {
                content["exit_code"] = exitCode;
            }
            foreach (var field in new[] { "stdout_head", "stdout_tail", "stderr_head", "stderr_tail" })
            {
                if (output[field] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text))
                {
                    continue;
                }
                content[field] = text.Length <= MaximumFailureDiagnosticLength
                    ? text
                    : field.EndsWith("_tail", StringComparison.Ordinal)
                        ? text[^MaximumFailureDiagnosticLength..]
                        : text[..MaximumFailureDiagnosticLength];
            }
        }

        return new ToolExecutionReceipt(
            execution.ExecutionId,
            execution.ToolCallId,
            ToolExecutionOutcome.Failed,
            code,
            null,
            sideEffectsPossible: false,
            modelContent: Encoding.UTF8.GetBytes(content.ToJsonString()),
            contentEncoding: HarnessPayloadEncoding.Json);
    }

    /// <summary>
    /// Bounds the authority's repair summary before it becomes model-visible:
    /// one line, at most <see cref="MaximumRepairHintLength"/> characters, and
    /// never a forbidden field name. Serializer output escapes content quotes as
    /// <c>\u0022</c>, so both spellings are normalized before matching, exactly
    /// like the NDJSON privacy guard.
    /// </summary>
    internal static string? BoundRepairHint(string? safeSummary)
    {
        if (string.IsNullOrWhiteSpace(safeSummary))
        {
            return null;
        }

        var cleaned = safeSummary.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (cleaned.Length == 0 || cleaned.Contains('\0'))
        {
            return null;
        }

        if (cleaned.Length > MaximumRepairHintLength)
        {
            cleaned = cleaned[..MaximumRepairHintLength];
        }

        var serialized = new JsonObject { ["hint"] = cleaned }.ToJsonString();
        var normalized = serialized
            .ToLowerInvariant()
            .Replace("\\u0022", "\"", StringComparison.Ordinal);
        return HarnessEventContractValidator.ContainsForbiddenField(normalized)
            ? null
            : cleaned;
    }

    /// <summary>
    /// Carries a terminal execution's retained-output identity into failure content
    /// so a failed command can still be inspected with mode read_output beyond
    /// the bounded diagnostic excerpts included in terminal failure receipts.
    /// </summary>
    internal static string? BoundSpoolId(JsonObject? output)
    {
        if (output?["spool_id"] is JsonValue value
            && value.TryGetValue<string>(out var spoolId)
            && !string.IsNullOrWhiteSpace(spoolId)
            && spoolId.Length <= 256
            && !spoolId.Contains('\0'))
        {
            return spoolId;
        }

        return null;
    }

    internal static JsonObject ParseArguments(JsonElement arguments) =>
        JsonNode.Parse(arguments.GetRawText())!.AsObject();

    internal static JsonObject ParseArgumentsJson(string json) =>
        JsonNode.Parse(json)!.AsObject();

    /// <summary>
    /// The run-open payload both factories hand the execution authority. The
    /// runtime's <c>RunOpen</c> path is the only lease validator; this helper
    /// exists so the wire and in-process paths cannot drift into two payloads.
    /// </summary>
    internal static JsonObject BuildRunOpenPayload(
        string sessionId,
        string runId,
        string canonicalRoot,
        PolicySnapshot policySnapshot)
    {
        var wire = PolicySnapshotMapper.ToWire(policySnapshot);
        return new JsonObject
        {
            ["run_id"] = runId,
            ["session_id"] = sessionId,
            ["workspace_id"] = wire.Workspace.WorkspaceId,
            ["canonical_root"] = canonicalRoot,
            ["volume_serial"] = wire.Workspace.VolumeSerialNumber,
            ["root_file_id"] = wire.Workspace.RootFileId,
            ["display_hash"] = wire.Workspace.DisplayIdentityHash,
            ["preset"] = wire.Preset,
            ["snapshot_version"] = wire.Version,
            ["snapshot_hash"] = policySnapshot.Sha256,
            ["expires_at"] = DateTimeOffset
                .UtcNow
                .Add(LeaseLifetime)
                .ToString("O", CultureInfo.InvariantCulture),
            ["snapshot"] = BrokerPolicyContracts.WriteSnapshot(wire),
        };
    }

    /// <summary>
    /// Whether two lease identities describe the same run scope. Lease id and
    /// generation are deliberately excluded: a reconnect or a rebind keeps the
    /// scope and may reuse the gateway and its cached approvals.
    /// </summary>
    internal static bool SameLeaseScope(
        BrokerRunLeaseIdentity left,
        BrokerRunLeaseIdentity right) =>
        string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
        && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
        && string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal)
        && string.Equals(left.CanonicalRoot, right.CanonicalRoot, StringComparison.Ordinal)
        && left.PolicyVersion == right.PolicyVersion
        && string.Equals(left.PolicySha256, right.PolicySha256, StringComparison.Ordinal)
        && string.Equals(left.BrokerUrl, right.BrokerUrl, StringComparison.Ordinal)
        && string.Equals(left.AuthTokenSha256, right.AuthTokenSha256, StringComparison.Ordinal);
}
