using System.Text.Json;
using System.Diagnostics;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentWorkspaceTests
{
    [Fact]
    public async Task Cleanup_unlinks_junction_without_traversing_its_external_target()
    {
        var snapshot = await new SubagentSnapshotCapture().CaptureAsync(Lease(), State, default);
        var manifest = await new SubagentWorktreeProvisioner().ProvisionAsync(snapshot, "agent_cleanup", default);
        var external = Path.Combine(_owned, "external-target");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "user.txt"), "preserve");
        var link = Path.Combine(manifest.WorkspaceRoot, "junction");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{external}\"", UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        var session = BrokerSession.CreateForHost(_owned, DateTimeOffset.UtcNow.AddMinutes(30));
        using var tools = new ToolExecutionService(session, Root);
        using var runtime = new BrokerExecutionRuntime(session, tools);
        await runtime.ReclaimSubagentWorkspaceAsync(CleanupSource(manifest), default);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(external, "user.txt")));
        Assert.False(Directory.Exists(manifest.WorkspaceRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_failure_retains_identity_for_retry_without_modifying_locked_files(bool metadataLocked)
    {
        var snapshot = await new SubagentSnapshotCapture().CaptureAsync(Lease(), State, default);
        var manifest = await new SubagentWorktreeProvisioner().ProvisionAsync(snapshot, "agent_cleanup", default);
        var source = CleanupSource(manifest);
        var path = metadataLocked ? Path.Combine(manifest.GitDirectory, "HEAD")
            : Path.Combine(Path.GetDirectoryName(snapshot.BaseDirectory)!, "readonly.txt");
        if (!metadataLocked) { File.WriteAllText(path, "preserve until retry"); File.SetAttributes(path, FileAttributes.ReadOnly); }
        FileStream? locked = metadataLocked ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        var session = BrokerSession.CreateForHost(_owned, DateTimeOffset.UtcNow.AddMinutes(30));
        using var tools = new ToolExecutionService(session, Root);
        using var runtime = new BrokerExecutionRuntime(session, tools);
        try
        {
            var error = await Assert.ThrowsAsync<SubagentWorkspaceException>(() => runtime.ReclaimSubagentWorkspaceAsync(source, default));
            Assert.Equal("subagent_cleanup_incomplete", error.Code);
            Assert.True(File.Exists(path));
            if (!metadataLocked) Assert.Equal("preserve until retry", File.ReadAllText(path));
        }
        finally
        {
            locked?.Dispose();
            if (!metadataLocked && File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        }
        await runtime.ReclaimSubagentWorkspaceAsync(source, default);
        Assert.False(Directory.Exists(Path.GetDirectoryName(snapshot.BaseDirectory)));
        Assert.False(Directory.Exists(manifest.GitDirectory));
    }

    private SubagentCollectionSource CleanupSource(SubagentWorkspaceManifest manifest) =>
        new("run_parent", "run_child", "child", manifest.AgentId, Root, manifest.WorkspaceRoot, State, JsonSerializer.Serialize(manifest));

    [Theory]
    [InlineData("complete")]
    [InlineData("missing_child")]
    [InlineData("busy")]
    [InlineData("foreign_busy")]
    [InlineData("nested_busy")]
    [InlineData("snapshot_busy")]
    [InlineData("metadata_busy")]
    [InlineData("prefix_neighbor")]
    [InlineData("unrelated_session")]
    [InlineData("wrong_source")]
    [InlineData("replaced_owner")]
    [InlineData("foreign_git")]
    [InlineData("missing_identity")]
    public async Task Cleanup_reclaims_only_proven_owned_artifacts_and_retains_parent(string scenario)
    {
        var before = (await Git("status", "--porcelain=v1")).Stdout;
        var snapshot = await new SubagentSnapshotCapture().CaptureAsync(Lease(), State, default);
        var manifest = await new SubagentWorktreeProvisioner().ProvisionAsync(snapshot, "agent_cleanup", default);
        var ownerDirectory = Path.GetDirectoryName(snapshot.BaseDirectory)!;
        if (scenario == "missing_child") Directory.Move(manifest.WorkspaceRoot, manifest.WorkspaceRoot + "-retired");
        if (scenario == "replaced_owner")
        {
            Directory.Move(ownerDirectory, ownerDirectory + "-retired");
            Directory.CreateDirectory(ownerDirectory);
            File.WriteAllText(Path.Combine(ownerDirectory, "user.txt"), "preserve");
        }
        if (scenario == "foreign_git") manifest = manifest with { GitDirectory = snapshot.GitDirectory };
        if (scenario == "missing_identity") manifest = manifest with { Snapshot = snapshot with { OwnedDirectoryIdentity = null } };
        var source = new SubagentCollectionSource("run_parent", "run_child", "child", "agent_cleanup", Root,
            manifest.WorkspaceRoot, State, JsonSerializer.Serialize(manifest));
        if (scenario == "wrong_source") source = source with { StateDirectory = Path.Combine(_owned, "foreign") };
        var session = BrokerSession.CreateForHost(_owned, DateTimeOffset.UtcNow.AddMinutes(30));
        using var tools = new ToolExecutionService(session, Root);
        using var runtime = new BrokerExecutionRuntime(session, tools);
        if (scenario == "busy") await runtime.OpenSessionAsync("child", new HashSet<string>(), default);
        if (scenario is "foreign_busy" or "nested_busy" or "snapshot_busy" or "metadata_busy" or "unrelated_session" or "prefix_neighbor")
        {
            var foreignRoot = scenario switch
            {
                "nested_busy" => Path.Combine(manifest.WorkspaceRoot, "active"),
                "snapshot_busy" => snapshot.BaseDirectory,
                "metadata_busy" => manifest.GitDirectory,
                "prefix_neighbor" => ownerDirectory + "-neighbor",
                "unrelated_session" => Root,
                _ => manifest.WorkspaceRoot,
            };
            Directory.CreateDirectory(foreignRoot);
            Assert.Null((await runtime.OpenRunAsync(new AgentEnvelope(2, "open", AgentMessageTypes.RunOpen, "foreign", null, 10000,
                EnvelopeTestSnapshots.RunOpenPayload("run_foreign", "foreign", foreignRoot, EnvelopeTestSnapshots.StandardEmpty())), default)).Error);
        }

        if (scenario is "complete" or "missing_child" or "unrelated_session" or "prefix_neighbor")
        {
            await runtime.ReclaimSubagentWorkspaceAsync(source, default);
            if (scenario is "unrelated_session" or "prefix_neighbor") Assert.True(session.Owns("foreign"));
            if (scenario == "prefix_neighbor") Assert.True(Directory.Exists(ownerDirectory + "-neighbor"));
            Assert.False(Directory.Exists(ownerDirectory));
            Assert.False(Directory.Exists(manifest.GitDirectory));
            Assert.DoesNotContain("agent_cleanup", (await Git("worktree", "list", "--porcelain")).Stdout);
            await runtime.ReclaimSubagentWorkspaceAsync(source, default);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AgentProtocolException>(() => runtime.ReclaimSubagentWorkspaceAsync(source, default));
            Assert.Equal(scenario is "busy" or "foreign_busy" or "nested_busy" or "snapshot_busy" or "metadata_busy" ? "subagent_cleanup_busy" : scenario == "wrong_source"
                ? "subagent_collection_source_invalid" : "subagent_cleanup_identity_changed", error.Code);
            Assert.True(Directory.Exists(ownerDirectory));
            Assert.True(File.Exists(Path.Combine(Root, "edit.txt")));
            if (scenario == "replaced_owner") Assert.Equal("preserve", File.ReadAllText(Path.Combine(ownerDirectory, "user.txt")));
            if (scenario is "foreign_busy" or "nested_busy" or "snapshot_busy" or "metadata_busy")
            {
                Assert.True(session.Owns("foreign"));
                await runtime.CloseSessionAsync("foreign", default);
                await runtime.ReclaimSubagentWorkspaceAsync(source, default);
                Assert.False(Directory.Exists(ownerDirectory));
            }
        }
        Assert.Equal(before, (await Git("status", "--porcelain=v1")).Stdout);
        Assert.Equal("clean\r\n", File.ReadAllText(Path.Combine(Root, "edit.txt")));
    }
}
