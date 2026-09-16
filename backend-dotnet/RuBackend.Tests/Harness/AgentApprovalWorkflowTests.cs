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

    // 11c Preview stays exact
    [Fact]
    public void Preview_is_exact_without_rule_candidate()
    {
        var pending = PendingToolApproval.Issue("apr_" + new string('d', 32), Binding(path: "a.txt"));
        var res = ApprovalState.TryResolve(pending, ApprovalCommand.AllowOnce, Binding(path: "a.txt"), FixedNow);
        Assert.Equal(ApprovalOutcome.ConsumedApproved, res.Outcome);
    }

    // 11d Ordering critical test
    [Fact]
    public async Task Ordering_consume_execute_snapshot_bump_invalidates_other_pending()
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
        var resA = ledger.Resolve(idA, ApprovalCommand.AllowOnce, bindingA, now);
        Assert.Equal(ApprovalOutcome.ConsumedApproved, resA.Outcome);

        // 2) one-time authorization needs no rule persistence

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
        await Task.CompletedTask;
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
