using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;

namespace RuBackend.Harness.Policy;

public sealed record ApprovalPersistOutcome(
    bool Authorized,
    string Status, // "authorized", "denied", "error"
    string? ErrorCode,
    JsonObject? MatcherPreview);

public sealed class HarnessApprovalService
{
    public HarnessApprovalService(
        HarnessSqliteOptions options,
        IHarnessKeyStore keyStore,
        IIdGenerator ids,
        ITimeProvider time)
    {
        _ = options ?? throw new ArgumentNullException(nameof(options));
        _ = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _ = ids ?? throw new ArgumentNullException(nameof(ids));
        _ = time ?? throw new ArgumentNullException(nameof(time));
    }

    // Used for testing with custom store that throws
    internal HarnessApprovalService(
        SqlitePolicyRuleStore store,
        IIdGenerator ids,
        ITimeProvider time)
    {
        _ = store ?? throw new ArgumentNullException(nameof(store));
        _ = ids ?? throw new ArgumentNullException(nameof(ids));
        _ = time ?? throw new ArgumentNullException(nameof(time));
    }

    public Task<ApprovalPersistOutcome> ProcessAsync(
        ToolApprovalBinding binding,
        ApprovalCommand command,
        WorkspaceId workspaceId,
        RunId? runId,
        CancellationToken cancellationToken,
        string? externalDirectory = null,
        bool externalDirectoryRequested = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        _ = workspaceId.Value;

        string Sanitize(string? v) => v is null ? string.Empty : v.Replace("*", string.Empty).Replace("?", string.Empty);

        var preview = new JsonObject
        {
            ["tool_id"] = binding.ToolId,
            ["tool_version"] = binding.ToolVersion,
            ["canonical_path"] = binding.CanonicalPath is null ? null : (JsonNode?)Sanitize(binding.CanonicalPath),
            ["resolved_executable"] = binding.ResolvedExecutable is null ? null : (JsonNode?)Sanitize(binding.ResolvedExecutable),
            ["argv_pattern"] = new JsonArray(binding.Argv.Select(Sanitize).Select(x => (JsonNode?)x).ToArray()),
            ["cwd_pattern"] = binding.Cwd is null ? null : (JsonNode?)Sanitize(binding.Cwd),
            ["operation_class"] = binding.OperationClass,
            ["network_intent"] = binding.NetworkIntent,
        };

        if (!command.IsAllow)
        {
            return Task.FromResult(new ApprovalPersistOutcome(
                Authorized: false,
                Status: "denied",
                ErrorCode: "approval_denied",
                MatcherPreview: preview));
        }

        // Pi-style: approvals never persist reusable rules. The one-time
        // token or session-directory grant is the whole authorization.
        return Task.FromResult(new ApprovalPersistOutcome(
            Authorized: true,
            Status: "authorized",
            ErrorCode: null,
            MatcherPreview: preview));
    }
}
