using System.Text.Json;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// ADR 0042: a workspace that is not a usable Git source gets a Ru-owned private base
/// repository instead of a refusal, and the workspace itself is never written.
/// </summary>
public sealed partial class SubagentWorkspaceTests
{
    private string Plain => Path.Combine(_owned, "plain");

    [Fact]
    public async Task A_plain_folder_gets_a_private_base_and_a_working_child_without_touching_the_folder()
    {
        Directory.CreateDirectory(Path.Combine(Plain, "src"));
        Directory.CreateDirectory(Path.Combine(Plain, "ignored"));
        File.WriteAllText(Path.Combine(Plain, ".gitignore"), "ignored/\n");
        File.WriteAllText(Path.Combine(Plain, "src", "edit.txt"), "clean\r\n");
        File.WriteAllText(Path.Combine(Plain, ".gitattributes"), "* text=auto eol=lf\n");
        File.WriteAllText(Path.Combine(Plain, "ignored", "secret.txt"), "never copied");

        var session = BrokerSession.CreateForHost(_owned, DateTimeOffset.UtcNow.AddMinutes(30));
        using var tools = new ToolExecutionService(session, Plain);
        using var runtime = new BrokerExecutionRuntime(session, tools);
        Assert.Null((await runtime.OpenRunAsync(new AgentEnvelope(2, "open", AgentMessageTypes.RunOpen, "parent", null, 10000,
            EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent", Plain, EnvelopeTestSnapshots.StandardEmpty())), default)).Error);
        var manifest = await runtime.ProvisionSubagentWorkspaceAsync("run_parent", "agent_test", State, default);

        var snapshot = manifest.Snapshot;
        Assert.NotNull(snapshot.PrivateRepository);
        var owned = Path.GetDirectoryName(snapshot.BaseDirectory)!;
        Assert.Equal(Path.Combine(owned, SubagentSnapshotCapture.PrivateRepositoryName), snapshot.PrivateRepository);
        Assert.StartsWith(owned, Path.GetFullPath(snapshot.GitDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(Plain, ".git")), "the workspace must not be initialized");
        Assert.DoesNotContain(snapshot.Files, file => file.RelativePath.StartsWith("ignored/", StringComparison.Ordinal));
        // The child sees the exact bytes, not an eol-normalized copy.
        Assert.Equal("clean\r\n", File.ReadAllText(Path.Combine(manifest.WorkspaceRoot, "src", "edit.txt")));

        await runtime.CloseSessionAsync("parent", default);
        File.WriteAllText(Path.Combine(manifest.WorkspaceRoot, "src", "edit.txt"), "finished child");
        File.WriteAllText(Path.Combine(manifest.WorkspaceRoot, "src", "added.txt"), "new file");
        var source = new SubagentCollectionSource("run_parent", "run_child", "child", "agent_test", Plain,
            manifest.WorkspaceRoot, State, JsonSerializer.Serialize(manifest));
        var changes = await runtime.CollectSubagentChangesAsync(source, 1, new Dictionary<string, string>(), default);
        Assert.Equal(["src/added.txt", "src/edit.txt"], changes.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal("clean\r\n", File.ReadAllText(Path.Combine(Plain, "src", "edit.txt")));

        await runtime.ReclaimSubagentWorkspaceAsync(source, default);
        Assert.False(Directory.Exists(owned), "cleanup removes the snapshot, private repository included");
        Assert.True(File.Exists(Path.Combine(Plain, "src", "edit.txt")));
    }

    [Fact]
    public void Git_sourced_snapshot_evidence_keeps_its_earlier_json_shape()
    {
        // Retained evidence from earlier versions is compared byte for byte after a
        // round trip, so the new field must not appear when it is unused.
        var snapshot = new SubagentSnapshot("snapshot_a", "run_parent", "C:\\w", "head", "C:\\w\\.git", "C:\\w\\.git",
            "id", "C:\\s\\snapshot_a\\base", []);
        Assert.DoesNotContain("PrivateRepository", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }
}
