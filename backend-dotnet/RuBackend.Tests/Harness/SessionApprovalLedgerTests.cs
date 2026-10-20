using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuBackend.Harness.Policy;
using Xunit;

using RuAgent.Protocol.Harness;
namespace RuBackend.Tests.Harness;

/// <summary>
/// The in-process session ledger behind "one allow is remembered for the
/// session": exact-intent matching, TTL, per-session cap, and the snapshot
/// materialization the runtime loop consults.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class SessionApprovalLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private const string SessionId = "0123456789abcdef0123456789abcdef";

    public SessionApprovalLedgerTests() => SessionApprovalLedger.ClearForTests();

    [Fact]
    public void Remembered_intent_survives_until_its_lifetime()
    {
        SessionApprovalLedger.Remember(SessionId, Matcher("one"), Now);

        var entries = SessionApprovalLedger.Entries(SessionId, Now.AddMinutes(1));

        var entry = Assert.Single(entries);
        Assert.Equal("bash", entry.Matcher.ToolId);
        Assert.Equal(new[] { "/c", "echo", "one" }, entry.Matcher.ArgvPattern);

        Assert.Empty(SessionApprovalLedger.Entries(
            SessionId,
            Now + SessionApprovalLedger.EntryLifetime + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Entries_are_capped_per_session()
    {
        for (var index = 0; index <= SessionApprovalLedger.MaxEntriesPerSession; index++)
        {
            SessionApprovalLedger.Remember(
                SessionId,
                Matcher("intent-" + index.ToString("D3")),
                Now.AddSeconds(index));
        }

        var entries = SessionApprovalLedger.Entries(SessionId, Now.AddMinutes(1));

        Assert.Equal(SessionApprovalLedger.MaxEntriesPerSession, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.Matcher.ArgvPattern!.Contains("intent-000"));
        Assert.Contains(entries, entry => entry.Matcher.ArgvPattern!.Contains("intent-001"));
    }

    [Fact]
    public void ClearSession_forgets_only_that_session()
    {
        SessionApprovalLedger.Remember(SessionId, Matcher("one"), Now);
        SessionApprovalLedger.Remember("fedcba9876543210fedcba9876543210", Matcher("one"), Now);

        SessionApprovalLedger.ClearSession(SessionId);

        Assert.Empty(SessionApprovalLedger.Entries(SessionId, Now.AddMinutes(1)));
        Assert.Single(SessionApprovalLedger.Entries("fedcba9876543210fedcba9876543210", Now.AddMinutes(1)));
    }

    [Fact]
    public void Apply_returns_the_durable_snapshot_when_nothing_is_remembered()
    {
        var durable = DurableSnapshot();

        var applied = SessionApprovalLedger.Apply(durable, SessionId, Now);

        Assert.Same(durable, applied);
    }

    [Fact]
    public void Apply_materializes_exact_intent_allow_rule_with_bumped_version()
    {
        var durable = DurableSnapshot();
        SessionApprovalLedger.Remember(SessionId, Matcher("one"), Now);

        var applied = SessionApprovalLedger.Apply(durable, SessionId, Now);

        Assert.NotNull(applied);
        Assert.True(applied!.Version > durable.Version);
        Assert.NotEqual(durable.Sha256, applied.Sha256);
        var rule = Assert.Single(applied.OrderedRules);
        Assert.Equal(PolicyRuleScope.Run, rule.Scope);
        Assert.Equal(PolicyDecision.Allow, rule.Decision);
        Assert.Equal("bash", rule.Matcher.ToolId);
        Assert.Equal("1.0.0", rule.Matcher.ToolVersion);
        Assert.Equal("cmd.exe", rule.Matcher.ResolvedExecutable);
        Assert.Equal(new[] { "/c", "echo", "one" }, rule.Matcher.ArgvPattern);
        Assert.Equal("terminal", rule.Matcher.OperationClass);
        Assert.Equal("none", rule.Matcher.NetworkIntent);
        Assert.Equal(Now + SessionApprovalLedger.EntryLifetime, rule.ExpiresAt);

        var rebuilt = SessionApprovalLedger.Apply(durable, SessionId, Now.AddSeconds(5));
        Assert.Equal(applied.Sha256, rebuilt!.Sha256);
    }

    [Fact]
    public void Apply_ignores_expired_entries()
    {
        var durable = DurableSnapshot();
        SessionApprovalLedger.Remember(SessionId, Matcher("one"), Now);

        var applied = SessionApprovalLedger.Apply(
            durable,
            SessionId,
            Now + SessionApprovalLedger.EntryLifetime + TimeSpan.FromSeconds(1));

        Assert.Same(durable, applied);
    }

    private static PolicySnapshot DurableSnapshot() =>
        PolicySnapshotCanonicalizer.Create(
            1,
            AgentPermissionPreset.Standard,
            new WorkspaceIdentity(
                new WorkspaceId("ws_11111111111111111111111111111111"),
                "volume-1",
                "root-file-1",
                new string('a', 64)),
            Array.Empty<PolicyRule>(),
            Now);

    private static PolicyMatcher Matcher(string marker) => new(
        ToolId: "bash",
        ToolVersion: "1.0.0",
        CanonicalPathPattern: null,
        ResolvedExecutable: "cmd.exe",
        ArgvPattern: new[] { "/c", "echo", marker },
        CwdPattern: null,
        OperationClass: "terminal",
        NetworkIntent: "none");
}
