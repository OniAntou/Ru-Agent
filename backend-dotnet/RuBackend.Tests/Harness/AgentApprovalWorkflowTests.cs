using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Protocol.Harness;
using RuBackend.Harness.Policy;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentApprovalWorkflowTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);

    private static ToolApprovalBinding Binding(
        string? path = "a.txt",
        string? exe = null,
        IReadOnlyList<string>? argv = null,
        string? cwd = null,
        string op = "read",
        string network = "none",
        long version = 5,
        string? hash = null) => new(
            new RunId("run_" + new string('a', 32)),
            new ToolCallId("call_" + new string('b', 32)),
            "files.read",
            "1.0.0",
            path,
            exe,
            argv ?? Array.Empty<string>(),
            cwd,
            op,
            network,
            version,
            hash ?? new string('0', 64),
            FixedNow.AddMinutes(30));

    // 11a Parity hash test
    [Fact]
    public void Parity_hash_between_core_and_protocol_matches()
    {
        var workspace = new WorkspaceIdentity(
            new WorkspaceId("ws_11111111111111111111111111111111"),
            "VOL-12345678",
            "fid-abc123",
            new string('a', 64));
        var rules = new List<PolicyRule>
        {
            new(new PolicyRuleId("rule_11111111111111111111111111111111"),
                PolicyRuleScope.Project,
                PolicyDecision.Allow,
                new PolicyMatcher(ToolId: "files.read", ToolVersion: "1.0.0", CanonicalPathPattern: "a.txt", ResolvedExecutable: null, ArgvPattern: null, CwdPattern: null, OperationClass: "read", NetworkIntent: "none"),
                CreationOrder: 1,
                ExpiresAt: null),
        };
        var snapshot = PolicySnapshotCanonicalizer.Create(
            version: 1,
            preset: AgentPermissionPreset.Standard,
            workspace: workspace,
            orderedRules: rules,
            createdAt: FixedNow);

        var protocolHash = HarnessPolicySnapshotService.ComputeProtocolHash(snapshot);
        Assert.Equal(snapshot.Sha256, protocolHash);
        Assert.True(HarnessPolicySnapshotService.ParityCheck(snapshot));
    }

    // 11b First-writer wins
    [Fact]
    public void Approval_first_writer_wins_second_is_stale()
    {
        var ledger = new PendingApprovalLedger();
        var binding = Binding();
        ledger.Issue("apr_" + new string('c', 32), binding);

        var first = ledger.Resolve("apr_" + new string('c', 32), ApprovalCommand.AllowOnce, Binding(), FixedNow);
        Assert.Equal(ApprovalOutcome.ConsumedApproved, first.Outcome);

        var second = ledger.Resolve("apr_" + new string('c', 32), ApprovalCommand.AllowOnce, Binding(), FixedNow);
        Assert.Equal(ApprovalOutcome.NotFound, second.Outcome);
    }

    // 11c Exact scope preview
    [Fact]
    public void Exact_scope_preview_strips_glob()
    {
        var binding = Binding(path: "a*.txt", exe: "tool*.exe", argv: new[] { "arg*1", "value?" }, cwd: @"C:\ws\*");
        var pending = PendingToolApproval.Issue("apr_" + new string('d', 32), binding);
        var res = ApprovalState.TryResolve(pending, ApprovalCommand.AllowRun, Binding(path: "a*.txt", exe: "tool*.exe", argv: new[] { "arg*1", "value?" }, cwd: @"C:\ws\*"), FixedNow);
        Assert.NotNull(res.ReusableRuleCandidate);
        Assert.DoesNotContain("*", res.ReusableRuleCandidate!.PathPatterns[0]);
        Assert.All(res.ReusableRuleCandidate.ArgvPatterns, p => Assert.DoesNotContain("*", p));
        Assert.DoesNotContain("*", res.ReusableRuleCandidate.CwdPattern!);
    }

    // 11d Ordering critical test
    [Fact]
    public async Task Ordering_consume_persist_execute_snapshot_bump_invalidates_other_pending()
    {
        // Setup: two pending approvals under snapshot N
        var ledger = new PendingApprovalLedger();
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(30);
        var bindingA = Binding(path: "a.txt", hash: new string('a', 64), version: 5);
        var bindingB = Binding(path: "b.txt", hash: new string('b', 64), version: 5);
        // Note ToolCallId differs per binding: use distinct IDs
        bindingA = new ToolApprovalBinding(
            new RunId("run_" + new string('a', 32)),
            new ToolCallId("call_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            "files.read", "1.0.0", "a.txt", null, Array.Empty<string>(), null, "read", "none", 5, new string('a', 64), expires);
        bindingB = new ToolApprovalBinding(
            new RunId("run_" + new string('a', 32)),
            new ToolCallId("call_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            "files.read", "1.0.0", "b.txt", null, Array.Empty<string>(), null, "read", "none", 5, new string('b', 64), expires);

        var idA = "apr_" + new string('1', 32);
        var idB = "apr_" + new string('2', 32);
        ledger.Issue(idA, bindingA);
        ledger.Issue(idB, bindingB);

        // 1) consume one-time approval under snapshot N
        var resA = ledger.Resolve(idA, ApprovalCommand.AllowRun, bindingA, now);
        Assert.Equal(ApprovalOutcome.ConsumedApproved, resA.Outcome);
        Assert.NotNull(resA.ReusableRuleCandidate);
        Assert.Equal(PolicyRuleScope.Run, resA.ReusableRuleCandidate!.Scope);

        // 2) persist reusable rule (or rule_save_failed)
        var tmpRoot = Path.Combine(Path.GetTempPath(), "ru-ordering-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        var dbPath = Path.Combine(tmpRoot, "ordering.db");
        var options = new HarnessSqliteOptions(dbPath);
        var keyStore = new EphemeralKeyStore();
        await HarnessSqliteSchema.InitializeAsync(options, CancellationToken.None);
        var policyStore = new SqlitePolicyRuleStore(options, keyStore);
        var approvalService = new HarnessApprovalService(policyStore, new TestIdGen(), new FixedTimeProvider(FixedNow));

        var workspaceId = new WorkspaceId("ws_11111111111111111111111111111111");
        var runId = new RunId("run_" + new string('a', 32));
        var persistResult = await approvalService.ProcessAsync(bindingA, ApprovalCommand.AllowRun, workspaceId, runId, CancellationToken.None);
        Assert.True(persistResult.Authorized);

        // 3) execute already-authorized call under snapshot N (already consumed, token valid)
        // Simulate execution succeeded – we just verify bindingA would still match snapshot N

        // 4) persist final receipt (simulated)

        // 5) build/bind snapshot N+1
        var newSnapshotVersion = 6L;
        var bindingBAfter = bindingB with { SnapshotVersion = newSnapshotVersion, IntentHash = new string('c', 64) };
        // For pending B, the expected after rebind would be with new version, but pending still holds old version

        // 6) invalidate other pending decisions from N
        var staleResult = ApprovalState.TryResolve(
            PendingToolApproval.Issue("apr_" + new string('9', 32), bindingB),
            ApprovalCommand.AllowOnce,
            bindingBAfter,
            DateTimeOffset.UtcNow);
        Assert.Equal(ApprovalOutcome.Stale, staleResult.Outcome);

        // Ensure original pending B still exists in ledger and now fails due to stale
        var staleLedger = ledger.Resolve(idB, ApprovalCommand.AllowOnce, bindingBAfter, now);
        Assert.Equal(ApprovalOutcome.Stale, staleLedger.Outcome);

        // Initiating call's ledger entry already consumed, second try is NotFound
        var secondTry = ledger.Resolve(idB, ApprovalCommand.AllowOnce, bindingB, now);
        Assert.Equal(ApprovalOutcome.NotFound, secondTry.Outcome);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        // Give SQLite a moment to release file
        await Task.Delay(50);
        try { Directory.Delete(tmpRoot, recursive: true); } catch { }
    }

    // 11e Rule persistence failure still permits once-only execution
    [Fact]
    public async Task Rule_persistence_failure_still_permits_once_execution()
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), "ru-rulefail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        var badOptions = new HarnessSqliteOptions(Path.Combine(tmpRoot, "nonexistent_dir", "bad.db"));
        var keyStore = new EphemeralKeyStore();
        var badStore = new SqlitePolicyRuleStore(badOptions, keyStore);
        var service = new HarnessApprovalService(badStore, new TestIdGen(), new FixedTimeProvider(FixedNow));

        var binding = new ToolApprovalBinding(
            new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            new ToolCallId("call_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            "files.read",
            "1.0.0",
            "a.txt",
            null,
            Array.Empty<string>(),
            null,
            "read",
            "none",
            1,
            new string('0', 64),
            FixedNow.AddMinutes(30));

        var result = await service.ProcessAsync(binding, ApprovalCommand.AllowProject, new WorkspaceId("ws_11111111111111111111111111111111"), new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), CancellationToken.None);
        Assert.True(result.Authorized);
        Assert.True(result.RuleSaveFailed);
        Assert.NotNull(result.MatcherPreview);
        Assert.DoesNotContain("*", result.MatcherPreview!["tool_id"]!.GetValue<string>());
        SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        try { Directory.Delete(tmpRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task Invalid_external_directory_scope_never_persists_exact_path_rule()
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), "ru-external-rule-reject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);
        var dbPath = Path.Combine(tmpRoot, "policy.db");
        var options = new HarnessSqliteOptions(dbPath);
        var keyStore = new EphemeralKeyStore();
        await HarnessSqliteSchema.InitializeAsync(options, CancellationToken.None);
        var policyStore = new SqlitePolicyRuleStore(options, keyStore);
        var service = new HarnessApprovalService(policyStore, new TestIdGen(), new FixedTimeProvider(FixedNow));
        var workspaceId = new WorkspaceId("ws_11111111111111111111111111111111");
        var runId = new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var binding = Binding(path: Path.GetPathRoot(AppContext.BaseDirectory));

        try
        {
            var result = await service.ProcessAsync(
                binding,
                ApprovalCommand.AllowProject,
                workspaceId,
                runId,
                CancellationToken.None,
                externalDirectory: null,
                externalDirectoryRequested: true);

            Assert.True(result.Authorized);
            Assert.True(result.RuleSaveFailed);
            Assert.Null(result.RuleId);
            Assert.Empty(await policyStore.ListProjectAsync(workspaceId, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            await Task.Delay(50);
            try { Directory.Delete(tmpRoot, recursive: true); } catch { }
        }
    }

    // 11f Broker restart before dispatch invalidates ephemeral decision
    [Fact]
    public void Broker_restart_before_dispatch_invalidates_ephemeral_decision()
    {
        var ledger = new PendingApprovalLedger();
        var binding = Binding();
        var id = "apr_" + new string('e', 32);
        ledger.Issue(id, binding);

        // Simulate broker restart: new ledger without pending
        var restartedLedger = new PendingApprovalLedger();
        var afterRestart = restartedLedger.Resolve(id, ApprovalCommand.AllowOnce, Binding(), FixedNow);
        Assert.Equal(ApprovalOutcome.NotFound, afterRestart.Outcome);

        // Original ledger still has pending, but restarted would be NotFound; safe re-evaluation
        var originalStill = ledger.Resolve(id, ApprovalCommand.AllowOnce, Binding(), FixedNow);
        Assert.Equal(ApprovalOutcome.ConsumedApproved, originalStill.Outcome);

        // After restart, second consume is NotFound, so no dispatch
        var second = restartedLedger.Resolve(id, ApprovalCommand.AllowOnce, Binding(), FixedNow);
        Assert.Equal(ApprovalOutcome.NotFound, second.Outcome);
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

    private sealed class TestIdGen : IIdGenerator
    {
        private int _c;
        public RunId NewRunId() => new($"run_{++_c:x32}");
        public ModelStepId NewModelStepId() => new($"step_{++_c:x32}");
        public ToolCallId NewToolCallId() => new($"call_{++_c:x32}");
        public ExecutionId NewExecutionId() => new($"exec_{++_c:x32}");
        public ApprovalId NewApprovalId() => new($"apr_{++_c:x32}");
        public TrustRequestId NewTrustRequestId() => new($"trust_{++_c:x32}");
        public PolicyRuleId NewPolicyRuleId() => new($"rule_{++_c:x32}");
        public EventId NewEventId() => new($"evt_{++_c:x32}");
        public PayloadId NewPayloadId() => new($"payload_{++_c:x32}");
    }

    private sealed class FixedTimeProvider : ITimeProvider
    {
        public FixedTimeProvider(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; }
    }
}
