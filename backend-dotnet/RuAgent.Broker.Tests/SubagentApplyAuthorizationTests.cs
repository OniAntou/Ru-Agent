using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentApplyAuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-apply-auth-" + Guid.NewGuid().ToString("N"));
    private readonly BrokerSession _session;
    private readonly ToolExecutionService _tools;
    private readonly BrokerExecutionRuntime _runtime;
    private readonly Journal _journal = new();

    public SubagentApplyAuthorizationTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "before");
        File.WriteAllText(Path.Combine(_root, "b.txt"), "before");
        _session = BrokerSession.CreateForHost(_root, DateTimeOffset.UtcNow.AddMinutes(30));
        _tools = new(_session, _root);
        _runtime = new(_session, _tools);
    }

    [Fact]
    public async Task Asked_files_require_exact_single_use_approval_before_any_write()
    {
        await Open(AskPolicy());
        var changes = Changes();
        var decision = await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default);
        Assert.Equal("ask", decision.Decision!.Decision);
        Assert.Equal("subagent_apply", decision.Decision.ApprovalKind);
        Assert.Null(decision.Decision.AuthorizationToken);
        await Denied(changes, "missing", "decision_stale");
        var approved = await Approve(decision.Decision.ApprovalId!, "allow_once");
        Assert.NotNull(approved.AuthorizationToken);
        Assert.Equal("before", File.ReadAllText(Path.Combine(_root, "a.txt")));
        var result = await _runtime.ApplySubagentChangesAsync("parent", "run_parent", changes,
            approved.AuthorizationToken!, _journal, default);
        Assert.Equal(SubagentApplyStatus.Applied, result.Status);
        Assert.Equal(2, _journal.Receipts);
        Assert.Equal("after", File.ReadAllText(Path.Combine(_root, "a.txt")));
        await Denied(changes, approved.AuthorizationToken!, "decision_stale", unchanged: false);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("version")]
    [InlineData("agent")]
    [InlineData("path")]
    public async Task Authorization_cannot_be_reused_for_changed_result(string variant)
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        var token = (await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default)).Decision!.AuthorizationToken!;
        var changed = variant switch
        {
            "version" => changes with { ResultVersion = 2 },
            "agent" => changes with { AgentId = "agent_other" },
            "path" => changes with { Files = [changes.Files[0] with { RelativePath = "c.txt" }] },
            _ => changes with { Files = [changes.Files[0] with
                { Content = Encoding.UTF8.GetBytes("other"), ProposedHash = FileHashService.ComputeStringHash("other") }] },
        };
        await Denied(changed, token, "decision_stale");
        Assert.Equal(0, _journal.Begins);
    }

    [Fact]
    public async Task Deny_on_later_file_wins_over_ask_without_issuing_approval()
    {
        var ask = PolicyEvalSnapshots.Rule("ask-a", "ask", PolicyEvalSnapshots.Matcher(toolId: "write", pathPattern: "a.txt"));
        var deny = PolicyEvalSnapshots.Rule("deny-b", "deny", PolicyEvalSnapshots.Matcher(toolId: "write", pathPattern: "b.txt"));
        await Open(EnvelopeTestSnapshots.StandardWithRules(ask, deny));
        var result = await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", Changes(), default);
        Assert.Equal("deny", result.Decision!.Decision);
        Assert.Null(result.Decision.ApprovalId);
        Assert.Null(result.Decision.AuthorizationToken);
        Assert.Equal(0, _journal.Begins);
    }

    [Fact]
    public async Task Snapshot_rebind_invalidates_approved_apply()
    {
        await Open(AskPolicy());
        var changes = Changes();
        var decision = await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default);
        var token = (await Approve(decision.Decision!.ApprovalId!, "allow_once")).AuthorizationToken!;
        var rebound = await _runtime.RebindRunPolicyAsync(Envelope("rebind", AgentMessageTypes.RunPolicyRebind,
            EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent", _root, EnvelopeTestSnapshots.StandardEmpty(2))), default);
        Assert.Null(rebound.Error);
        await Denied(changes, token, "decision_stale");
    }

    [Fact]
    public async Task Closing_parent_invalidates_old_apply_tokens_until_fresh_authorization()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        var token = (await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default)).Decision!.AuthorizationToken!;
        await _runtime.CloseRunAsync(Envelope("close", AgentMessageTypes.RunClose, new() { ["run_id"] = "run_parent" }), default);
        await Denied(changes, token, "subagent_apply_denied");
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        await Denied(changes, token, "decision_stale");
        Assert.Equal(0, _journal.Begins);
        var fresh = (await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default)).Decision!.AuthorizationToken!;
        var applied = await _runtime.ApplySubagentChangesAsync("parent", "run_parent", changes, fresh, _journal, default);
        Assert.Equal(SubagentApplyStatus.Applied, applied.Status);
    }

    [Fact]
    public async Task Approval_cannot_remember_or_grant_a_directory_for_a_result_batch()
    {
        await Open(AskPolicy());
        var decision = await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", Changes(), default);
        foreach (var command in new[] { "allow_always", "allow_session_directory" })
            Assert.Equal("invalid_command", (await Approve(decision.Decision!.ApprovalId!, command)).ErrorCode);
        Assert.NotNull((await Approve(decision.Decision!.ApprovalId!, "allow_once")).AuthorizationToken);
    }

    [Fact]
    public async Task Parent_edit_after_approval_conflicts_before_first_write()
    {
        await Open(AskPolicy());
        var changes = Changes();
        var decision = await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default);
        var token = (await Approve(decision.Decision!.ApprovalId!, "allow_once")).AuthorizationToken!;
        File.WriteAllText(Path.Combine(_root, "b.txt"), "user edit");
        var outcome = await _runtime.ApplySubagentChangesAsync("parent", "run_parent", changes, token, _journal, default);
        Assert.Equal(SubagentApplyStatus.Conflict, outcome.Status);
        Assert.Equal(0, _journal.Receipts);
        Assert.Equal("before", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("user edit", File.ReadAllText(Path.Combine(_root, "b.txt")));
    }

    private async Task Denied(SubagentChangeSet changes, string token, string code, bool unchanged = true)
    {
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.ApplySubagentChangesAsync("parent", "run_parent", changes, token, _journal, default));
        Assert.Equal(code, error.Code);
        if (unchanged) Assert.Equal("before", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Journal_failure_is_unknown_and_a_duplicate_cannot_repeat_the_write(bool afterWrite)
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        async Task<SubagentApplyOutcome> Apply()
        {
            var token = (await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default)).Decision!.AuthorizationToken!;
            return await _runtime.ApplySubagentChangesAsync("parent", "run_parent", changes, token, _journal, default);
        }
        _journal.FailIntent = !afterWrite;
        _journal.FailReceipt = afterWrite;
        Assert.Equal(SubagentApplyStatus.Unknown, (await Apply()).Status);
        Assert.Equal(0, _journal.Receipts);
        Assert.Equal(afterWrite ? "after" : "before", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("before", File.ReadAllText(Path.Combine(_root, "b.txt")));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "new user edit");
        Assert.Equal(SubagentApplyStatus.Unknown, (await Apply()).Status);
        Assert.Equal("new user edit", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Missing_final_durable_outcome_raises_explicit_uncertainty_and_pending_never_replays()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        async Task<SubagentApplyOutcome> Apply()
        {
            var token = (await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default)).Decision!.AuthorizationToken!;
            return await _runtime.ApplySubagentChangesAsync("parent", "run_parent", changes, token, _journal, default);
        }
        _journal.FailFinish = true;
        var error = await Assert.ThrowsAsync<SubagentApplyInterruptedException>(Apply);
        Assert.Equal(SubagentApplyStatus.Unknown, error.Outcome.Status);
        Assert.Equal("subagent_apply_reconciliation_required", error.Outcome.Code);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(SubagentApplyStatus.Pending, _journal.Outcome!.Status);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "new user edit");
        Assert.Equal(SubagentApplyStatus.Unknown, (await Apply()).Status);
        Assert.Equal(2, _journal.Receipts);
        Assert.Equal("new user edit", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Policy_change_after_intent_prevents_the_first_write()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        var token = (await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default)).Decision!.AuthorizationToken!;
        var reboundSucceeded = false;
        _journal.BeforeIntent = async () =>
        {
            var rebound = await _runtime.RebindRunPolicyAsync(Envelope("rebind", AgentMessageTypes.RunPolicyRebind,
                EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent", _root, EnvelopeTestSnapshots.StandardEmpty(2))), default);
            reboundSucceeded = rebound.Error is null;
        };
        var outcome = await _runtime.ApplySubagentChangesAsync("parent", "run_parent", changes, token, _journal, default);
        Assert.Equal(SubagentApplyStatus.Unknown, outcome.Status);
        Assert.True(reboundSucceeded);
        Assert.Equal("subagent_apply_reconciliation_required", outcome.Code);
        Assert.Equal(0, _journal.Receipts);
        Assert.Equal("before", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Stop_between_files_cancels_remaining_writes_and_never_replays()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        var token = (await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default)).Decision!.AuthorizationToken!;
        _journal.AfterReceipt = () => _runtime.CancelRun("run_parent");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _runtime.ApplySubagentChangesAsync("parent", "run_parent", changes, token, _journal, default));
        Assert.Equal(SubagentApplyStatus.Unknown, _journal.Outcome!.Status);
        Assert.Equal(1, _journal.Receipts);
        Assert.Equal("after", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal("before", File.ReadAllText(Path.Combine(_root, "b.txt")));
        await Denied(changes, token, "subagent_apply_denied", unchanged: false);
    }

    [Fact]
    public async Task Apply_uses_frozen_bytes_even_if_caller_mutates_the_source_during_journaling()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var bytes = Encoding.UTF8.GetBytes("after");
        var changes = Changes() with { Files = [Changes().Files[0] with { Content = bytes }] };
        var token = (await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default)).Decision!.AuthorizationToken!;
        _journal.BeforeIntent = () => { bytes.AsSpan().Fill((byte)'x'); return Task.CompletedTask; };
        Assert.Equal(SubagentApplyStatus.Applied,
            (await _runtime.ApplySubagentChangesAsync("parent", "run_parent", changes, token, _journal, default)).Status);
        Assert.Equal("after", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Fresh_authorization_of_already_applied_result_returns_receipt_without_writing_again()
    {
        await Open(EnvelopeTestSnapshots.StandardEmpty());
        var changes = Changes();
        async Task<SubagentApplyOutcome> Apply()
        {
            var token = (await _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", changes, default)).Decision!.AuthorizationToken!;
            return await _runtime.ApplySubagentChangesAsync("parent", "run_parent", changes, token, _journal, default);
        }
        Assert.Equal(SubagentApplyStatus.Applied, (await Apply()).Status);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "later user edit");
        Assert.Equal(SubagentApplyStatus.Applied, (await Apply()).Status);
        Assert.Equal(2, _journal.Receipts);
        Assert.Equal("later user edit", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Plan_root_cannot_evaluate_or_apply_even_with_forged_token()
    {
        await Open(EnvelopeTestSnapshots.Snapshot(1, "plan", []));
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.EvaluateSubagentApplyAsync("parent", "run_parent", Changes(), default));
        Assert.Equal("subagent_apply_denied", error.Code);
        await Denied(Changes(), "forged", "subagent_apply_denied");
        Assert.Equal(0, _journal.Begins);
    }

    [Theory]
    [InlineData(SubagentRole.Coding)]
    [InlineData(SubagentRole.Review)]
    public async Task Delegated_child_cannot_apply_even_with_full_access(SubagentRole role)
    {
        await Open(EnvelopeTestSnapshots.FullAccessEmpty());
        var child = Path.Combine(_root, "child");
        Directory.CreateDirectory(child);
        var opened = await _runtime.OpenRunAsync(new(2, "open-child", AgentMessageTypes.RunOpen, "child", null, 10000,
            EnvelopeTestSnapshots.RunOpenPayload("run_child", "child", role == SubagentRole.Review ? _root : child,
                EnvelopeTestSnapshots.FullAccessEmpty())), default);
        Assert.Null(opened.Error);
        await _runtime.BindDelegationAsync("run_parent", "run_child", role, default);
        var changes = Changes() with { ParentRunId = "run_child", ParentRoot = role == SubagentRole.Review ? _root : child };
        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.EvaluateSubagentApplyAsync("child", "run_child", changes, default));
        Assert.Equal("subagent_apply_denied", error.Code);
        error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            _runtime.ApplySubagentChangesAsync("child", "run_child", changes, "forged", _journal, default));
        Assert.Equal("subagent_apply_denied", error.Code);
        Assert.Equal(0, _journal.Begins);
    }

    private static BrokerPolicySnapshotWire AskPolicy() => EnvelopeTestSnapshots.StandardWithRules(
        PolicyEvalSnapshots.Rule("ask-write", "ask", PolicyEvalSnapshots.Matcher(toolId: "write")));
    private async Task Open(BrokerPolicySnapshotWire policy)
    {
        var result = await _runtime.OpenRunAsync(Envelope("open", AgentMessageTypes.RunOpen,
            EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent", _root, policy)), default);
        Assert.Null(result.Error);
    }
    private Task<ApprovalDecisionResult> Approve(string id, string command) => _runtime.RespondApprovalAsync(
        Envelope("approve", AgentMessageTypes.ApprovalRespond,
            new() { ["run_id"] = "run_parent", ["approval_id"] = id, ["command"] = command }), default);
    private static AgentEnvelope Envelope(string id, string type, JsonObject payload) => new(2, id, type, "parent", null, 10000, payload);
    private SubagentChangeSet Changes() => new("agent_one", "run_parent", _root, "snapshot_one", 1,
        new[] { "a.txt", "b.txt" }.Select(path => new SubagentFileChange(path, FileHashService.ComputeStringHash("before"),
            FileHashService.ComputeStringHash("after"), Encoding.UTF8.GetBytes("after"), 100644)).ToArray());

    private sealed class Journal : ISubagentApplyJournal
    {
        public int Begins { get; private set; }
        public int Receipts { get; private set; }
        public SubagentApplyOutcome? Outcome { get; private set; }
        public Func<Task>? BeforeIntent { get; set; }
        public Action? AfterReceipt { get; set; }
        public bool FailIntent { get; set; }
        public bool FailReceipt { get; set; }
        public bool FailFinish { get; set; }
        public Task<SubagentApplyOutcome?> BeginAsync(SubagentChangeSet changes, string fingerprint, CancellationToken ct)
        { Begins++; var previous = Outcome; Outcome ??= new(SubagentApplyStatus.Pending, 0, "pending"); return Task.FromResult(previous); }
        public Task RecordFileIntentAsync(SubagentChangeSet changes, int fileIndex, CancellationToken ct) =>
            FailIntent ? throw new InvalidOperationException("intent unavailable") : BeforeIntent?.Invoke() ?? Task.CompletedTask;
        public Task RecordFileReceiptAsync(SubagentChangeSet changes, int fileIndex, CancellationToken ct)
        { if (FailReceipt) throw new InvalidOperationException("receipt unavailable"); Receipts++; AfterReceipt?.Invoke(); return Task.CompletedTask; }
        public Task FinishAsync(SubagentChangeSet changes, SubagentApplyOutcome outcome, CancellationToken ct)
        { if (FailFinish) throw new InvalidOperationException("finish unavailable"); Outcome = outcome; return Task.CompletedTask; }
    }

    public void Dispose()
    {
        _runtime.Dispose();
        _tools.Dispose();
        Directory.Delete(_root, true);
    }
}
