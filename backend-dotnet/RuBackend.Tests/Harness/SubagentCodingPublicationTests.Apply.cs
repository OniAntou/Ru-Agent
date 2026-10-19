using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class SubagentCodingPublicationTests
{
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
}
