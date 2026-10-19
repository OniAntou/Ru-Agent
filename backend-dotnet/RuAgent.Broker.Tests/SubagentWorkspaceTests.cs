using RuAgent.Protocol.Subagents;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Subagents.Integration;
using RuAgent.Broker.Tools.Git;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

[Collection(nameof(GitProcessHeavyCollection))]
public sealed partial class SubagentWorkspaceTests : IAsyncLifetime
{
    private readonly string _owned = Path.Combine(Path.GetTempPath(), "ru-subagent-fixture-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_owned, "repo");
    private string State => Path.Combine(_owned, "state");
    private readonly IGitCli _git = new StreamingGitCli();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        await Git("init");
        File.WriteAllText(Path.Combine(Root, ".gitignore"), "ignored/\n");
        File.WriteAllText(Path.Combine(Root, "edit.txt"), "clean\r\n");
        File.WriteAllText(Path.Combine(Root, "delete.txt"), "delete me");
        await Git("add", ".");
        await Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "seed");
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("busy")]
    [InlineData("wrong_agent")]
    [InlineData("foreign_root")]
    [InlineData("foreign_storage")]
    [InlineData("malformed_entry")]
    public async Task Trusted_collection_reads_only_the_frozen_owned_worktree_after_session_release(string scenario)
    {
        var session = BrokerSession.CreateForHost(_owned, DateTimeOffset.UtcNow.AddMinutes(30));
        using var tools = new ToolExecutionService(session, Root);
        using var runtime = new BrokerExecutionRuntime(session, tools);
        Assert.Null((await runtime.OpenRunAsync(new AgentEnvelope(2, "open", AgentMessageTypes.RunOpen, "parent", null, 10000,
            EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent", Root, EnvelopeTestSnapshots.StandardEmpty())), default)).Error);
        var manifest = await runtime.ProvisionSubagentWorkspaceAsync("run_parent", "agent_test", State, default);
        await runtime.CloseSessionAsync("parent", default);
        File.WriteAllText(Path.Combine(manifest.WorkspaceRoot, "edit.txt"), "finished child");
        var source = new SubagentCollectionSource("run_parent", "run_child", "child", "agent_test", Root,
            manifest.WorkspaceRoot, State, System.Text.Json.JsonSerializer.Serialize(manifest));
        if (scenario == "busy") await runtime.OpenSessionAsync("child", new HashSet<string>(), default);
        if (scenario == "wrong_agent") source = source with { AgentId = "agent_foreign" };
        if (scenario == "foreign_root") source = source with { WorkspaceRoot = Root };
        if (scenario == "malformed_entry") source = source with
        {
            WorktreeManifestJson = System.Text.Json.JsonSerializer.Serialize(manifest with
            { Snapshot = manifest.Snapshot with { Files = [new(null!, null!, 100644, 0)] } }),
        };
        if (scenario == "foreign_storage") source = source with { StateDirectory = Path.Combine(_owned, "elsewhere") };
        if (scenario != "closed")
        {
            await Assert.ThrowsAsync<AgentProtocolException>(() => runtime.CollectSubagentChangesAsync(source, 1,
                new Dictionary<string, string>(), default));
            return;
        }
        var changes = await runtime.CollectSubagentChangesAsync(source, 1, new Dictionary<string, string>(), default);
        Assert.Single(changes.Files);
        Assert.Equal("edit.txt", changes.Files[0].RelativePath);
        Assert.Equal("finished child", System.Text.Encoding.UTF8.GetString(changes.Files[0].Content.Span));
        Assert.Equal("clean\r\n", File.ReadAllText(Path.Combine(Root, "edit.txt")));
    }

    [Fact]
    public async Task Dirty_bytes_and_untracked_files_survive_without_changing_parent_index()
    {
        File.WriteAllText(Path.Combine(Root, "edit.txt"), "staged\r\n");
        await Git("add", "edit.txt");
        File.WriteAllBytes(Path.Combine(Root, "edit.txt"), [0, 255, 13, 10, 42]);
        File.Delete(Path.Combine(Root, "delete.txt"));
        File.WriteAllText(Path.Combine(Root, "new.txt"), "untracked\r\n");
        Directory.CreateDirectory(Path.Combine(Root, "ignored"));
        File.WriteAllText(Path.Combine(Root, "ignored", "private.txt"), "do not copy");
        var index = File.ReadAllBytes(Path.Combine(Root, ".git", "index"));
        var head = (await Git("rev-parse", "HEAD")).Stdout;
        var session = BrokerSession.CreateForHost(_owned, DateTimeOffset.UtcNow.AddMinutes(30));
        using var tools = new ToolExecutionService(session, Root);
        using var runtime = new BrokerExecutionRuntime(session, tools);
        var open = await runtime.OpenRunAsync(new AgentEnvelope(2, "open", AgentMessageTypes.RunOpen, "parent", null, 10000,
            EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent", Root, EnvelopeTestSnapshots.StandardEmpty())), default);
        Assert.Null(open.Error);
        var workspace = await runtime.ProvisionSubagentWorkspaceAsync("run_parent", "agent_test", State, default);
        var snapshot = workspace.Snapshot;
        Assert.Equal(new byte[] { 0, 255, 13, 10, 42 }, File.ReadAllBytes(Path.Combine(workspace.WorkspaceRoot, "edit.txt")));
        Assert.Equal("untracked\r\n", File.ReadAllText(Path.Combine(workspace.WorkspaceRoot, "new.txt")));
        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot, "delete.txt")));
        Assert.False(Directory.Exists(Path.Combine(workspace.WorkspaceRoot, "ignored")));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(Root, ".git", "index")));
        Assert.Equal(head, (await Git("rev-parse", "HEAD")).Stdout);
        Assert.NotEqual(snapshot.BaseDirectory, workspace.WorkspaceRoot);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot, "new.txt"), "child edit");
        var changes = await new SubagentChangeSetCollector().CollectAsync(workspace, 1, default);
        Assert.Single(changes.Files);
        Assert.Equal("new.txt", changes.Files[0].RelativePath);
        Assert.Equal("child edit", System.Text.Encoding.UTF8.GetString(changes.Files[0].Content.Span));
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot, "edit.txt"), "follow-up");
        var followUp = await new SubagentChangeSetCollector().CollectAsync(workspace, 2, default,
            new Dictionary<string, string> { ["new.txt"] = changes.Files[0].ProposedHash });
        Assert.Single(followUp.Files);
        Assert.Equal("edit.txt", followUp.Files[0].RelativePath);
    }

    [Fact]
    public async Task Repository_hooks_filters_and_fsmonitor_are_not_executed()
    {
        var sentinel = Path.Combine(_owned, "executed.txt").Replace('\\', '/');
        var hook = Path.Combine(Root, ".git", "hooks", "post-checkout");
        File.WriteAllText(hook, $"#!/bin/sh\necho unsafe > '{sentinel}'\n");
        await Git("config", "core.fsmonitor", hook.Replace('\\', '/'));
        await Git("config", "filter.evil.smudge", hook.Replace('\\', '/'));
        File.WriteAllText(Path.Combine(Root, ".gitattributes"), "*.txt filter=evil\n");
        var snapshot = await new SubagentSnapshotCapture().CaptureAsync(Lease(), State, default);
        await new SubagentWorktreeProvisioner().ProvisionAsync(snapshot, "agent_test", default);
        Assert.False(File.Exists(sentinel));
    }

    [Fact]
    public async Task Denied_file_does_not_produce_incomplete_successful_snapshot()
    {
        var rules = new[] { PolicyEvalSnapshots.Rule("deny-read", "deny", PolicyEvalSnapshots.Matcher(toolId: "read")) };
        var error = await Assert.ThrowsAsync<SubagentWorkspaceException>(() =>
            new SubagentSnapshotCapture().CaptureAsync(Lease(rules), State, default));
        Assert.Equal("subagent_snapshot_read_denied", error.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(State));
    }

    [Fact]
    public async Task Snapshot_does_not_inherit_parent_remembered_read_approval()
    {
        var deny = PolicyEvalSnapshots.Rule("deny-read", "deny", PolicyEvalSnapshots.Matcher(toolId: "read"));
        var allow = PolicyEvalSnapshots.Rule("parent-approved", "allow", PolicyEvalSnapshots.Matcher(toolId: "read"), scope: "run");
        var ceiling = EnvelopeTestSnapshots.StandardWithRules(deny);
        var session = BrokerSession.CreateForHost(_owned, DateTimeOffset.UtcNow.AddMinutes(30));
        using var tools = new ToolExecutionService(session, Root);
        using var runtime = new BrokerExecutionRuntime(session, tools);
        var open = await runtime.OpenRunAsync(new AgentEnvelope(2, "open", AgentMessageTypes.RunOpen, "parent", null, 10000,
            EnvelopeTestSnapshots.RunOpenPayload("run_parent", "parent", Root, EnvelopeTestSnapshots.Snapshot(2, "standard", [deny, allow]))), default);
        Assert.Null(open.Error);
        var error = await Assert.ThrowsAsync<SubagentWorkspaceException>(() => runtime.ProvisionSubagentWorkspaceAsync(
            "run_parent", "agent_test", State, default, ceiling));
        Assert.Equal("subagent_snapshot_read_denied", error.Code);
    }

    private BrokerRunLease Lease(RuAgent.Protocol.Harness.BrokerPolicyRuleWire[]? rules = null)
    {
        var wire = EnvelopeTestSnapshots.StandardWithRules(rules ?? []);
        var bound = BoundPolicySnapshot.Parse(wire);
        return new BrokerRunLease("run_parent", "parent", bound.WorkspaceId, Root, bound.VolumeSerialNumber,
            bound.RootFileId, bound.DisplayIdentityHash, bound.Preset, bound.Version, bound.Sha256,
            DateTimeOffset.UtcNow.AddMinutes(15), [], bound, 1);
    }

    private async Task<GitCliResult> Git(params string[] args)
    {
        var result = await _git.RunAsync(new[] { "-c", "core.hooksPath=NUL" }.Concat(args).ToArray(), Root, default);
        Assert.True(result.Succeeded, result.FailureReason + ": " + result.Stderr);
        return result;
    }

    public Task DisposeAsync()
    {
        foreach (var file in Directory.EnumerateFiles(_owned, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_owned, true);
        return Task.CompletedTask;
    }
}
