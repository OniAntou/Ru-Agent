using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;

namespace RuBackend.Harness.Policy;

public sealed record ApprovalPersistOutcome(
    bool Authorized,
    string Status, // "authorized", "denied", "error"
    string? ErrorCode,
    JsonObject? MatcherPreview,
    JsonObject? ReusableCandidate,
    bool RuleSaveFailed,
    string? RuleId);

public sealed class HarnessApprovalService
{
    private static readonly (string ToolId, string OperationClass)[] ExternalFileOperations =
    [
        ("files.list", "list"),
        ("files.glob", "glob"),
        ("files.search", "search"),
        ("files.read", "read"),
        ("files.write", "write"),
        ("files.edit", "edit"),
    ];

    private readonly HarnessSqliteOptions _options;
    private readonly IHarnessKeyStore _keyStore;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;

    public HarnessApprovalService(
        HarnessSqliteOptions options,
        IHarnessKeyStore keyStore,
        IIdGenerator ids,
        ITimeProvider time)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    // Used for testing with custom store that throws
    internal HarnessApprovalService(
        SqlitePolicyRuleStore store,
        IIdGenerator ids,
        ITimeProvider time)
    {
        _storeOverride = store;
        _ids = ids;
        _time = time;
        _options = new HarnessSqliteOptions(":memory:");
        _keyStore = new EphemeralKeyStore();
    }

    private readonly SqlitePolicyRuleStore? _storeOverride;

    private SqlitePolicyRuleStore Store => _storeOverride ?? new SqlitePolicyRuleStore(_options, _keyStore);

    public async Task<ApprovalPersistOutcome> ProcessAsync(
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
            return new ApprovalPersistOutcome(
                Authorized: false,
                Status: "denied",
                ErrorCode: "approval_denied",
                MatcherPreview: preview,
                ReusableCandidate: null,
                RuleSaveFailed: false,
                RuleId: null);
        }

        JsonObject? candidateJson = null;
        bool ruleSaveFailed = false;
        string? ruleId = null;

        if (command.Kind == ApprovalCommandKind.AllowRun
            || command.Kind == ApprovalCommandKind.AllowProject)
        {
            var scope = command.Kind == ApprovalCommandKind.AllowRun ? "run" : "project";
            var externalDirectoryPattern = externalDirectory is not null
                && ExternalDirectoryPolicy.TryCreatePattern(externalDirectory, out var generatedPattern)
                    ? generatedPattern
                    : null;
            var isTerminal = binding.OperationClass == "terminal";
            var reusableExternalDirectory = externalDirectoryPattern is not null;
            var pathPatterns = externalDirectoryRequested && !reusableExternalDirectory
                ? Array.Empty<string>()
                : binding.CanonicalPath is null
                    ? Array.Empty<string>()
                    : new[] { externalDirectoryPattern ?? Sanitize(binding.CanonicalPath) };
            var cwdPattern = binding.Cwd is null
                ? null
                : externalDirectoryPattern is not null && isTerminal
                    ? externalDirectoryPattern
                    : Sanitize(binding.Cwd);
            var argvPatterns = binding.Argv.Select(Sanitize).ToArray();

            candidateJson = new JsonObject
            {
                ["scope"] = scope,
                ["tool_id"] = binding.ToolId,
                ["tool_version"] = binding.ToolVersion,
                ["path_patterns"] = new JsonArray(pathPatterns.Select(p => (JsonNode?)p).ToArray()),
                ["cwd_pattern"] = cwdPattern is null ? null : (JsonNode?)cwdPattern,
                ["argv_patterns"] = new JsonArray(argvPatterns.Select(p => (JsonNode?)p).ToArray()),
            };

            // A request classified as external must never fall back to an exact
            // reusable rule when its canonical directory cannot be represented as
            // a safe descendant pattern (for example a filesystem root or a
            // directory that disappeared during approval). The one-time intent
            // bridge still authorizes the already-approved call; only durable
            // broadening is refused.
            if (externalDirectoryRequested && !reusableExternalDirectory)
            {
                ruleSaveFailed = true;
            }

            // Try to persist reusable rule
            if (!externalDirectoryRequested || reusableExternalDirectory)
            {
                try
                {
                    var policyScope = scope == "run" ? PolicyRuleScope.Run : PolicyRuleScope.Project;
                    var store = Store;
                    var rules = new List<PolicyRule>();
                    if (externalDirectoryPattern is not null
                        && !isTerminal
                        && binding.ToolId.StartsWith("files.", StringComparison.Ordinal))
                    {
                        // An external-directory approval is a boundary grant, not a
                        // one-tool grant. Materialize the safe, non-destructive file
                        // operations as one transaction; delete/restore remain
                        // approval actions and terminal commands keep their own rules.
                        foreach (var (toolId, operationClass) in ExternalFileOperations)
                        {
                            rules.Add(new PolicyRule(
                                RuleId: _ids.NewPolicyRuleId(),
                                Scope: policyScope,
                                Decision: PolicyDecision.Allow,
                                Matcher: new PolicyMatcher(
                                    ToolId: toolId,
                                    ToolVersion: binding.ToolVersion,
                                    CanonicalPathPattern: externalDirectoryPattern,
                                    OperationClass: operationClass,
                                    NetworkIntent: binding.NetworkIntent),
                                CreationOrder: 0,
                                ExpiresAt: null));
                        }
                    }
                    else
                    {
                        rules.Add(new PolicyRule(
                            RuleId: _ids.NewPolicyRuleId(),
                            Scope: policyScope,
                            Decision: PolicyDecision.Allow,
                            Matcher: new PolicyMatcher(
                                ToolId: binding.ToolId,
                                ToolVersion: binding.ToolVersion,
                                CanonicalPathPattern: pathPatterns.Length == 0 ? null : pathPatterns[0],
                                ResolvedExecutable: binding.ResolvedExecutable is null ? null : Sanitize(binding.ResolvedExecutable),
                                ArgvPattern: argvPatterns.Length == 0 ? null : argvPatterns,
                                CwdPattern: cwdPattern,
                                OperationClass: binding.OperationClass,
                                NetworkIntent: binding.NetworkIntent),
                            CreationOrder: 0,
                            ExpiresAt: null));
                    }

                    ruleId = rules[0].RuleId.Value;
                    if (policyScope == PolicyRuleScope.Run)
                    {
                        if (runId is null) throw new HarnessStoreException("policy_scope_ownership_invalid");
                        await store.CreateManyAsync(rules, null, runId.Value, _time.UtcNow, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await store.CreateManyAsync(rules, workspaceId, null, _time.UtcNow, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is HarnessStoreException or InvalidOperationException or System.Data.Common.DbException or Microsoft.Data.Sqlite.SqliteException)
                {
                    ruleSaveFailed = true;
                }
            }
        }

        return new ApprovalPersistOutcome(
            Authorized: true,
            Status: "authorized",
            ErrorCode: null,
            MatcherPreview: preview,
            ReusableCandidate: candidateJson,
            RuleSaveFailed: ruleSaveFailed,
            RuleId: ruleId);
    }

    private sealed class EphemeralKeyStore : IHarnessKeyStore
    {
        private readonly byte[] _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken)
        {
            var copy = new byte[32];
            Buffer.BlockCopy(_key, 0, copy, 0, 32);
            return ValueTask.FromResult(copy);
        }
    }
}
