extern alias Broker;

using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Broker;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentCodingPublicationTests
{
    [Fact]
    public async Task Evaluating_apply_for_one_root_does_not_block_an_unrelated_root()
    {
        await using var storage = await BackendHarnessTestFixture.CreateAsync();
        var roots = new[]
        {
            await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
                "run_" + new string('a', 32), new string('a', 32)),
            await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
                "run_" + new string('b', 32), new string('b', 32)),
        };
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        var results = new SqliteSubagentResultStore(storage.Options, storage.PayloadStore);
        var protector = new WorkspaceJournalProtector(storage.KeyStore);
        var changes = new SqliteSubagentChangeSetStore(storage.Options, protector);
        for (var index = 0; index < roots.Length; index++)
        {
            var root = roots[index].Run;
            var suffix = (char)('c' + index);
            var childRunId = new RunId("run_" + new string(suffix, 32));
            var agentId = "agent_" + new string(suffix, 32);
            var child = new AgentRun(childRunId, new(new string(suffix, 32)), root.WorkspaceId,
                new(HarnessRunState.Created), 0, 1, 1, 0, 0, root.Preset, root.PolicySnapshotVersion,
                root.PolicySnapshotSha256, BackendHarnessTestFixture.Timestamp, BackendHarnessTestFixture.Timestamp);
            var eventId = new EventId("evt_" + new string(suffix, 32));
            var childCommand = new NewAgentRun(child,
                [new(eventId, HarnessEventKind.RunStateChanged)], [], [],
                new(new string('a', 64), new string('b', 64), 1));
            await agents.CreateAsync(new(agentId, root.RunId, "spawn_" + agentId, SubagentRole.Coding, childCommand), default);
            var activationRun = new AgentRun(child.RunId, child.SessionId, child.WorkspaceId,
                new(HarnessRunState.Running), child.Version + 1, child.LastSequence + 1, child.BudgetWindow,
                child.ModelStepsInWindow, child.ToolCallsInWindow, child.Preset, child.PolicySnapshotVersion,
                child.PolicySnapshotSha256, child.CreatedAt, BackendHarnessTestFixture.Timestamp);
            var activated = await storage.EventStore.CommitAsync(new(child.RunId, child.Version, activationRun,
                [new(new("evt_" + new string((char)('e' + index), 32)), HarnessEventKind.RunStateChanged)], [], []), default);
            Assert.Equal(HarnessCommitOutcome.Committed, activated.Outcome);
            var completed = await CompleteAsync(storage, activated.Run!);
            var bytes = System.Text.Encoding.UTF8.GetBytes("new file");
            var proposedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            await changes.SaveAsync(childRunId, new(agentId, root.RunId.Value, storage.RootPath,
                "snapshot_" + new string(suffix, 32), 1,
                [new("file.txt", new string('0', 64), proposedHash, bytes, 100644)]), default);
            await results.PublishTerminalAsync(root.RunId, agentId, completed.RunId, default);
        }

        var gateway = new BlockingApplyEvaluationFactory();
        using var service = new SubagentApplyService(storage.EventStore, agents, results, changes,
            new SqliteSubagentApplyJournal(storage.Options, protector), gateway);
        ToolProposal Proposal(int index)
        {
            var suffix = (char)('c' + index);
            return new(roots[index].Run.RunId, new("step_" + new string(suffix, 32)),
                new("call_" + new string(suffix, 32)), "provider_apply", 1, "agent_apply", "1.0.0",
                JsonSerializer.SerializeToElement(new
                    { agent_id = "agent_" + new string(suffix, 32), result_version = 1 }));
        }

        var first = service.EvaluateAsync(Proposal(0), default);
        await gateway.RequestEntered(1).WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.EvaluateAsync(Proposal(1), default);
        var unrelatedRootEntered = false;
        try
        {
            await gateway.RequestEntered(2).WaitAsync(TimeSpan.FromSeconds(2));
            unrelatedRootEntered = true;
        }
        catch (TimeoutException) { }
        finally
        {
            gateway.ReleaseRequest(1);
            gateway.ReleaseRequest(2);
        }

        await Task.WhenAll(first, second);
        Assert.True(unrelatedRootEntered, "The second root waited for the first root's apply evaluation to finish.");

        var sameRootFirst = service.EvaluateAsync(Proposal(0), default);
        await gateway.RequestEntered(3).WaitAsync(TimeSpan.FromSeconds(5));
        var sameRootSecond = service.EvaluateAsync(Proposal(0), default);
        var sameRootOverlap = false;
        try
        {
            await gateway.RequestEntered(4).WaitAsync(TimeSpan.FromSeconds(2));
            sameRootOverlap = true;
        }
        catch (TimeoutException) { }
        finally { gateway.ReleaseRequest(3); }
        await sameRootFirst;
        await gateway.RequestEntered(4).WaitAsync(TimeSpan.FromSeconds(5));
        gateway.ReleaseRequest(4);
        await sameRootSecond;
        Assert.False(sameRootOverlap, "Two operations for the same root reached the authority at once.");

        var activeOperation = service.EvaluateAsync(Proposal(0), default);
        await gateway.RequestEntered(5).WaitAsync(TimeSpan.FromSeconds(5));
        using var canceledWaiter = new CancellationTokenSource();
        var canceled = service.EvaluateAsync(Proposal(0), canceledWaiter.Token);
        canceledWaiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        gateway.ReleaseRequest(5);
        await activeOperation;
        var afterCanceledWaiter = service.EvaluateAsync(Proposal(0), default);
        await gateway.RequestEntered(6).WaitAsync(TimeSpan.FromSeconds(5));
        gateway.ReleaseRequest(6);
        await afterCanceledWaiter;
    }

    private static async Task VerifyApplyAsync(string scenario, BackendHarnessTestFixture storage,
        InProcessExecutionFixture execution, AgentRun root, SubagentRecord agent, string repo, PolicySnapshot policy)
    {
        var nativeReceipt = scenario.StartsWith("apply_native_", StringComparison.Ordinal);
        if (nativeReceipt) scenario = scenario.Replace("apply_native_", "apply_", StringComparison.Ordinal);
        var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
        var protector = new WorkspaceJournalProtector(storage.KeyStore);
        var results = new SqliteSubagentResultStore(storage.Options, storage.PayloadStore);
        var changes = new SqliteSubagentChangeSetStore(storage.Options, protector);
        var journal = new SqliteSubagentApplyJournal(storage.Options, protector);
        if (scenario == "apply_ask")
            policy = PolicySnapshotCanonicalizer.Create(2, policy.Preset, policy.Workspace,
                [new(new("rule_" + new string('d', 32)), PolicyRuleScope.Project, PolicyDecision.Ask,
                    new(ToolId: "write"), 1, null)], DateTimeOffset.UtcNow);
        await execution.Factory.CreateAsync(repo, policy, root.RunId, root.SessionId.Value, default);
        using var apply = new SubagentApplyService(storage.EventStore, agents, results, changes, journal, execution.Factory);
        var arguments = System.Text.Json.JsonSerializer.SerializeToElement(new
            { agent_id = scenario == "apply_foreign" ? "agent_foreign" : agent.AgentId, result_version = 1 });
        var proposal = new ToolProposal(root.RunId, new("step_" + new string('b', 32)),
            new("call_" + new string('c', 32)), "provider_apply", 1, "agent_apply", "1.0.0", arguments);
        async Task<SubagentApplyOutcome> Execute(ToolProposalDecision decision)
        {
            if (!nativeReceipt) return await apply.ApplyAsync(proposal, decision.CanonicalIntentToken!, decision.GatewayGeneration, default);
            var prepared = new PreparedToolExecution(new("exec_" + Guid.NewGuid().ToString("N")), proposal.ToolCallId,
                proposal.ModelOrder, ToolAccessKind.Mutation, decision.CanonicalIntentToken!, [], decision.GatewayGeneration);
            var receipt = await apply.ApplyReceiptAsync(proposal, prepared, default);
            Assert.Equal(prepared.ExecutionId, receipt.ExecutionId);
            Assert.Equal(proposal.ToolCallId, receipt.ToolCallId);
            Assert.Equal(proposal.ProviderCallId, receipt.ProviderCallId);
            using var output = JsonDocument.Parse(receipt.ModelContent);
            var value = output.RootElement;
            var status = Enum.Parse<SubagentApplyStatus>(value.GetProperty("status").GetString()!, ignoreCase: true);
            Assert.Equal(status == SubagentApplyStatus.Unknown ? ToolExecutionOutcome.OutcomeUnknown
                : status == SubagentApplyStatus.Applied ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed, receipt.Outcome);
            Assert.Equal(status != SubagentApplyStatus.Conflict, receipt.SideEffectsPossible);
            return new(status, value.GetProperty("applied_files").GetInt32(), value.GetProperty("code").GetString()!);
        }
        if (scenario == "apply_terminal") await CompleteAsync(storage, (await storage.EventStore.GetRunAsync(root.RunId, default))!);
        if (scenario is "apply_foreign" or "apply_terminal")
        {
            var denied = await Assert.ThrowsAsync<HarnessStoreException>(() => apply.EvaluateAsync(proposal, default));
            Assert.Equal(scenario == "apply_foreign" ? "subagent_apply_ownership_denied" : "subagent_apply_root_inactive", denied.Code);
            Assert.Null(await journal.ReadAsync(root.RunId.Value, agent.AgentId, 1, default));
        }
        else
        {
            async Task<ToolProposalDecision> Authorize()
            {
                var evaluated = await apply.EvaluateAsync(proposal, default);
                if (scenario == "apply_ask")
                {
                    Assert.Equal(PolicyDecision.Ask, evaluated.Decision);
                    Assert.Equal("subagent_apply_approval_required", evaluated.SafeCode);
                    Assert.False((await execution.Factory.RespondApprovalAsync(root.RunId.Value,
                        evaluated.ApprovalId!.Value.Value, "allow_always", default)).Authorized);
                    Assert.True((await execution.Factory.RespondApprovalAsync(root.RunId.Value,
                        evaluated.ApprovalId!.Value.Value, "allow_once", default)).Authorized);
                    evaluated = await apply.EvaluateAsync(proposal, default);
                }
                return evaluated;
            }
            var decision = await Authorize();
            Assert.Equal(PolicyDecision.Allow, decision.Decision);
            Assert.Equal(ToolAccessKind.Mutation, decision.AccessKind);
            if (scenario == "apply_conflict") await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "new parent edit");
            if (scenario is "apply_receipt_failure" or "apply_finish_failure")
            {
                await using var connection = await storage.Options.OpenConnectionAsync(default);
                using var failpoint = connection.CreateCommand();
                failpoint.CommandText = scenario == "apply_receipt_failure"
                    ? "CREATE TRIGGER fixture_fail_receipt BEFORE UPDATE OF receipted ON agent_subagent_apply_files BEGIN SELECT RAISE(FAIL,'fixture_receipt_failure'); END"
                    : "CREATE TRIGGER fixture_fail_finish BEFORE UPDATE OF status ON agent_subagent_apply_operations BEGIN SELECT RAISE(FAIL,'fixture_finish_failure'); END";
                await failpoint.ExecuteNonQueryAsync();
            }
            SubagentApplyOutcome outcome;
            if (scenario == "apply_finish_failure" && !nativeReceipt)
                outcome = (await Assert.ThrowsAsync<SubagentApplyInterruptedException>(() =>
                    apply.ApplyAsync(proposal, decision.CanonicalIntentToken!, decision.GatewayGeneration, default))).Outcome;
            else outcome = await Execute(decision);
            var expectedStatus = scenario switch
            {
                "apply_conflict" => SubagentApplyStatus.Conflict,
                "apply_receipt_failure" or "apply_finish_failure" => SubagentApplyStatus.Unknown,
                _ => SubagentApplyStatus.Applied,
            };
            Assert.Equal(expectedStatus, outcome.Status);
            var saved = (await journal.ReadAsync(root.RunId.Value, agent.AgentId, 1, default))!;
            if (scenario == "apply_finish_failure") Assert.Equal(SubagentApplyStatus.Pending, saved.Outcome.Status);
            else Assert.Equal(outcome, saved.Outcome);
            if (scenario == "apply_receipt_failure") Assert.False(Assert.Single(saved.Files).Receipted);
            var inspected = await apply.InspectAsync(proposal, default);
            Assert.Equal(saved.Fingerprint, inspected.Fingerprint);
            var observed = Assert.Single(inspected.Files);
            Assert.Equal(scenario == "apply_conflict" ? "diverged" : "proposed", observed.Match);
            Assert.Equal(saved.Files.Count > 0 && saved.Files[0].Receipted, observed.Receipted);
            Assert.Equal(saved.Outcome, (await journal.ReadAsync(root.RunId.Value, agent.AgentId, 1, default))!.Outcome);
            Assert.Equal(scenario == "apply_conflict" ? "new parent edit" : "child bytes\r\n",
                await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
            // A duplicate command reads the durable outcome; newer user bytes survive.
            await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "later edit");
            var replay = await Authorize();
            var repeated = await Execute(replay);
            Assert.Equal(outcome.Status, repeated.Status);
            if (scenario != "apply_finish_failure") Assert.Equal(outcome, repeated);
            Assert.Equal("later edit", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt")));
        }
    }

    private sealed class BlockingApplyEvaluationFactory : IToolGatewayFactory
    {
        private int _count;
        private readonly TaskCompletionSource[] _entered = Enumerable.Range(0, 8)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private readonly TaskCompletionSource[] _release = Enumerable.Range(0, 8)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        public bool IsConfigured => true;
        public Task RequestEntered(int requestNumber) => _entered[requestNumber - 1].Task;
        public void ReleaseRequest(int requestNumber) => _release[requestNumber - 1].TrySetResult();

        public async Task<ToolProposalDecision> EvaluateSubagentApplyAsync(ToolProposal proposal,
            SubagentChangeSet changes, CancellationToken ct)
        {
            var count = Interlocked.Increment(ref _count);
            _entered[count - 1].TrySetResult();
            await _release[count - 1].Task.WaitAsync(ct);
            return ToolProposalDecision.Allow(ToolAccessKind.Mutation, "intent");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<IToolGateway> CreateAsync(string workspaceRoot, PolicySnapshot policySnapshot, RunId runId,
            string? agentSessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JsonObject> ListSessionTasksAsync(string runId, string? killExecutionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(bool Authorized, string? AuthorizationToken, Broker::RuAgent.Broker.Policy.ApprovalRememberPlan? RememberPlan)> RespondApprovalAsync(string runId, string hostApprovalId,
            string command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CloseRunAsync(string runId) => throw new NotSupportedException();
    }
}
