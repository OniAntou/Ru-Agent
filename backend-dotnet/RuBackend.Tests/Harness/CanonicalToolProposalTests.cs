using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuBackend.Harness.Broker;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class CanonicalToolProposalTests
{
    [Fact]
    public async Task Delete_rejects_preimage_hash_in_model_arguments()
    {
        using var fixture = WorkspaceFixture.Create("old");
        var gateway = new HarnessToolGateway(fixture.Root, new[] { "files.delete" });
        var proposal = Proposal("files.delete", "{\"path\":\"note.txt\",\"expected_sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}");

        var decision = await gateway.EvaluateAsync(proposal, CancellationToken.None);

        Assert.Equal(PolicyDecision.Deny, decision.Decision);
        Assert.Equal("invalid_arguments", decision.SafeCode);
    }

    [Fact]
    public async Task Search_accepts_only_the_canonical_is_regex_argument()
    {
        using var fixture = WorkspaceFixture.Create("needle");
        var gateway = new HarnessToolGateway(fixture.Root, new[] { "files.search" });
        var valid = Proposal("files.search", "{\"query\":\"needle\",\"is_regex\":false}");
        var legacy = Proposal("files.search", "{\"query\":\"needle\",\"regex\":false}");

        Assert.Equal(PolicyDecision.Allow, (await gateway.EvaluateAsync(valid, CancellationToken.None)).Decision);
        var legacyDecision = await gateway.EvaluateAsync(legacy, CancellationToken.None);
        Assert.Equal(PolicyDecision.Deny, legacyDecision.Decision);
        Assert.Equal("invalid_arguments", legacyDecision.SafeCode);
    }

    private static ToolProposal Proposal(string toolId, string arguments) => new(
        new RunId("run_11111111111111111111111111111111"),
        new ModelStepId("step_11111111111111111111111111111111"),
        new ToolCallId("call_11111111111111111111111111111111"),
        1,
        toolId,
        "1.0.0",
        JsonDocument.Parse(arguments).RootElement);

    private sealed class WorkspaceFixture : IDisposable
    {
        private WorkspaceFixture(string root) => Root = root;
        public string Root { get; }

        public static WorkspaceFixture Create(string content)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-canonical-tools-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "note.txt"), content);
            return new WorkspaceFixture(root);
        }

        public void Dispose() => Directory.Delete(Root, true);
    }
}
