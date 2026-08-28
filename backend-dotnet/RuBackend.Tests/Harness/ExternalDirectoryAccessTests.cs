using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Reconciliation;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class ExternalDirectoryAccessTests
{
    [Fact]
    public async Task External_directory_grant_allows_directory_and_descendants_but_not_siblings()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-external-directory-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "workspace");
        var external = Path.Combine(tempRoot, "external");
        var sibling = Path.Combine(tempRoot, "external-sibling");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(external, "nested"));
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(external, "one.txt"), "one");
        File.WriteAllText(Path.Combine(external, "nested", "two.txt"), "two");
        File.WriteAllText(Path.Combine(sibling, "not-allowed.txt"), "no");

        try
        {
            var runId = new RunId("run_11111111111111111111111111111111");
            var grants = new InMemoryAgentApprovalGrants();
            var gateway = new HarnessToolGateway(
                workspace,
                new[] { "files.list", "files.read" },
                presetProvider: _ => AgentPermissionPreset.Standard,
                idGenerator: new CryptographicIdGenerator(),
                approvalGrants: grants);

            var firstProposal = Proposal(
                runId,
                "call_11111111111111111111111111111111",
                "files.read",
                $"{{\"path\":\"{EscapeJson(Path.Combine(external, "one.txt"))}\"}}");
            var beforeGrant = await gateway.EvaluateAsync(firstProposal, CancellationToken.None);
            Assert.Equal(RuAgent.Harness.Core.Policy.PolicyDecision.Ask, beforeGrant.Decision);
            Assert.Equal("external_directory_required", beforeGrant.SafeCode);

            grants.GrantExternalDirectory(runId.Value, external);

            var allowedFile = await gateway.EvaluateAsync(firstProposal, CancellationToken.None);
            Assert.Equal(RuAgent.Harness.Core.Policy.PolicyDecision.Allow, allowedFile.Decision);
            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    firstProposal,
                    new ExecutionId("exec_11111111111111111111111111111111"),
                    allowedFile.CanonicalIntentToken!),
                CancellationToken.None);
            var receipt = await gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
            using (var resultJson = JsonDocument.Parse(receipt.ModelContent.ToArray()))
            {
                Assert.Equal(
                    Path.GetFullPath(Path.Combine(external, "one.txt")).Replace('\\', '/'),
                    resultJson.RootElement.GetProperty("path").GetString());
            }

            var nestedProposal = Proposal(
                runId,
                "call_22222222222222222222222222222222",
                "files.read",
                $"{{\"path\":\"{EscapeJson(Path.Combine(external, "nested", "two.txt"))}\"}}");
            var allowedNestedFile = await gateway.EvaluateAsync(nestedProposal, CancellationToken.None);
            Assert.Equal(RuAgent.Harness.Core.Policy.PolicyDecision.Allow, allowedNestedFile.Decision);

            var directoryProposal = Proposal(
                runId,
                "call_33333333333333333333333333333333",
                "files.list",
                $"{{\"path\":\"{EscapeJson(external)}\"}}");
            var allowedDirectory = await gateway.EvaluateAsync(directoryProposal, CancellationToken.None);
            Assert.Equal(RuAgent.Harness.Core.Policy.PolicyDecision.Allow, allowedDirectory.Decision);

            var siblingProposal = Proposal(
                runId,
                "call_44444444444444444444444444444444",
                "files.read",
                $"{{\"path\":\"{EscapeJson(Path.Combine(sibling, "not-allowed.txt"))}\"}}");
            var siblingDecision = await gateway.EvaluateAsync(siblingProposal, CancellationToken.None);
            Assert.Equal(RuAgent.Harness.Core.Policy.PolicyDecision.Ask, siblingDecision.Decision);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task External_directory_grant_allows_create_edit_but_keeps_delete_approval_gated()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-external-mutation-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "workspace");
        var external = Path.Combine(tempRoot, "external");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(external);
        var createdFile = Path.Combine(external, "created.txt");

        try
        {
            var runId = new RunId("run_55555555555555555555555555555555");
            var grants = new InMemoryAgentApprovalGrants();
            var gateway = new HarnessToolGateway(
                workspace,
                new[] { "files.write", "files.edit", "files.delete" },
                presetProvider: _ => AgentPermissionPreset.Standard,
                idGenerator: new CryptographicIdGenerator(),
                approvalGrants: grants);

            var writeProposal = Proposal(
                runId,
                "call_55555555555555555555555555555555",
                "files.write",
                JsonSerializer.Serialize(new { path = createdFile, content = "created outside workspace" }));
            var beforeGrant = await gateway.EvaluateAsync(writeProposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, beforeGrant.Decision);
            Assert.Equal("external_directory_required", beforeGrant.SafeCode);

            grants.GrantExternalDirectory(runId.Value, external);
            var allowedWrite = await gateway.EvaluateAsync(writeProposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, allowedWrite.Decision);
            var prepared = await gateway.PrepareAsync(
                new ApprovedToolExecution(
                    writeProposal,
                    new ExecutionId("exec_55555555555555555555555555555555"),
                    allowedWrite.CanonicalIntentToken!),
                CancellationToken.None);
            var receipt = await gateway.ExecuteAsync(
                prepared,
                _ => ValueTask.CompletedTask,
                CancellationToken.None);
            Assert.Equal(ToolExecutionOutcome.Succeeded, receipt.Outcome);
            Assert.Equal("created outside workspace", File.ReadAllText(createdFile));

            var deleteProposal = Proposal(
                runId,
                "call_66666666666666666666666666666666",
                "files.delete",
                JsonSerializer.Serialize(new { path = createdFile }));
            var deleteDecision = await gateway.EvaluateAsync(deleteProposal, CancellationToken.None);
            Assert.Equal(PolicyDecision.Ask, deleteDecision.Decision);
            Assert.Equal("approval_required", deleteDecision.SafeCode);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void External_directory_grant_is_scoped_to_run_and_directory_boundary()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-external-grant-store-" + Guid.NewGuid().ToString("N"));
        var external = Path.Combine(tempRoot, "external");
        Directory.CreateDirectory(Path.Combine(external, "nested"));

        try
        {
            var grants = new InMemoryAgentApprovalGrants();
            grants.GrantExternalDirectory(
                "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                external);

            Assert.True(grants.IsExternalDirectoryGranted(
                "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine(external, "nested", "file.txt")));
            Assert.False(grants.IsExternalDirectoryGranted(
                "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine(tempRoot, "external-sibling", "file.txt")));
            Assert.False(grants.IsExternalDirectoryGranted(
                "run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                Path.Combine(external, "nested", "file.txt")));
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task Persisted_external_directory_rule_matches_directory_and_descendants()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "harness-external-policy-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(tempRoot, "workspace");
        var external = Path.Combine(tempRoot, "external");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(external, "nested"));
        File.WriteAllText(Path.Combine(external, "nested", "two.txt"), "two");

        try
        {
            var normalizedExternal = external.Replace('\\', '/');
            var workspaceIdentity = new WorkspaceIdentity(
                new WorkspaceId("ws_22222222222222222222222222222222"),
                "volume",
                "root-file",
                new string('a', 64));
            var snapshot = PolicySnapshotCanonicalizer.Create(
                2,
                AgentPermissionPreset.Standard,
                workspaceIdentity,
                new[]
                {
                    new PolicyRule(
                        new PolicyRuleId("rule_22222222222222222222222222222222"),
                        PolicyRuleScope.Project,
                        PolicyDecision.Allow,
                        new PolicyMatcher(
                            ToolId: "files.list",
                            CanonicalPathPattern: normalizedExternal + "/**",
                            OperationClass: "list"),
                        1,
                        null),
                    new PolicyRule(
                        new PolicyRuleId("rule_33333333333333333333333333333333"),
                        PolicyRuleScope.Project,
                        PolicyDecision.Allow,
                        new PolicyMatcher(
                            ToolId: "files.read",
                            CanonicalPathPattern: normalizedExternal + "/**",
                            OperationClass: "read"),
                        2,
                        null),
                },
                DateTimeOffset.UtcNow);
            var gateway = new HarnessToolGateway(
                workspace,
                new[] { "files.list", "files.read" },
                presetProvider: _ => AgentPermissionPreset.Standard,
                idGenerator: new CryptographicIdGenerator(),
                policySnapshot: snapshot);
            var runId = new RunId("run_22222222222222222222222222222222");

            var directoryDecision = await gateway.EvaluateAsync(
                Proposal(
                    runId,
                    "call_55555555555555555555555555555555",
                    "files.list",
                    $"{{\"path\":\"{EscapeJson(external)}\"}}"),
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, directoryDecision.Decision);

            var nestedDecision = await gateway.EvaluateAsync(
                Proposal(
                    runId,
                    "call_66666666666666666666666666666666",
                    "files.read",
                    $"{{\"path\":\"{EscapeJson(Path.Combine(external, "nested", "two.txt"))}\"}}"),
                CancellationToken.None);
            Assert.Equal(PolicyDecision.Allow, nestedDecision.Decision);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static ToolProposal Proposal(RunId runId, string callId, string toolId, string arguments)
    {
        return new ToolProposal(
            runId,
            new ModelStepId("step_11111111111111111111111111111111"),
            new ToolCallId(callId),
            1,
            toolId,
            "1.0.0",
            JsonDocument.Parse(arguments).RootElement);
    }

    private static string EscapeJson(string value) =>
        JsonSerializer.Serialize(value)[1..^1];
}
