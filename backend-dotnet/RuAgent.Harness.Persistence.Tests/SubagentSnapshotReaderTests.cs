using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed partial class SubagentSnapshotReaderTests
{
    [Fact]
    public async Task Snapshot_exposes_owned_attempts_and_exact_task_without_private_model_or_context_data()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        var bytes = ToolArgumentsCodec.Serialize("provider_spawn", "agent_spawn", "1.0.0",
            JsonSerializer.SerializeToElement(new { role = "coding", task = "Implement Unicode paging", context = "PRIVATE_SELECTED_CONTEXT" }));
        var payload = new PayloadId("payload_" + new string('a', 32));
        var invocation = "call_" + new string('a', 32);
        var created = new NewAgentRun(root.Run,
            [new(HarnessStoreTestFixture.Event(1), HarnessEventKind.ToolProposed, payloadId: payload,
                modelStepId: new("step_" + new string('a', 32)), toolCallId: new(invocation), providerCallId: "provider_spawn")],
            [new(payload, HarnessPayloadKind.ToolArguments, HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json, bytes.Length, bytes)], [], root.CreationIdentity);
        await fixture.CreateEventStore().TryCreateRunAsync(created, default);
        var child = fixture.NewRun(2, 2, 1);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, invocation, SubagentRole.Coding, child), default);
        await new SqliteAgentRunProviderSelectionStore(fixture.Options).PinAsync(child.Run.RunId,
            new("OpenAI", "fixture", "chat_completions", "https://private-fixture.invalid/v1/", "pinned-model",
                "private_account_api_key", "{\"temperature\":0.7}", null), default);
        var snapshot = await new SqliteSubagentSnapshotReader(fixture.Options, fixture.PayloadStore).ReadAsync(root.Run.RunId, default);
        var agent = Assert.Single(snapshot.Agents);
        Assert.Equal("agent_one", agent.Agent.AgentId);
        Assert.Equal("Implement Unicode paging", agent.Task);
        var attempt = Assert.Single(agent.Attempts);
        Assert.Equal("pinned-model", attempt.Model!.Model);
        Assert.Equal(child.Run.RunId, attempt.Attempt.Run.RunId);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("PRIVATE_SELECTED_CONTEXT", json);
        Assert.DoesNotContain("private_account_api_key", json);
        Assert.DoesNotContain("private-fixture.invalid", json);
        Assert.DoesNotContain("temperature", json);
    }

    [Fact]
    public async Task Missing_child_pin_is_explicit_and_internal_or_deleted_roots_are_hidden()
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var root = fixture.NewRun(1, 1, 1);
        await fixture.CreateEventStore().TryCreateRunAsync(root, default);
        var child = fixture.NewRun(2, 2, 1);
        await new SqliteSubagentStore(fixture.Options, fixture.PayloadStore).CreateAsync(
            new("agent_one", root.Run.RunId, "spawn_one", SubagentRole.Review, child), default);
        await new SqliteAgentRunProviderSelectionStore(fixture.Options).PinAsync(root.Run.RunId,
            new("OpenAI", "parent", "chat_completions", "https://parent.invalid/v1/", "parent-model",
                "parent_api_key", "{}", null), default);
        var reader = new SqliteSubagentSnapshotReader(fixture.Options, fixture.PayloadStore);
        var agent = Assert.Single((await reader.ReadAsync(root.Run.RunId, default)).Agents);
        Assert.Null(Assert.Single(agent.Attempts).Model);
        Assert.Null(agent.Task);
        var nested = await Assert.ThrowsAsync<HarnessStoreException>(() => reader.ReadAsync(child.Run.RunId, default));
        Assert.Equal("subagent_root_not_found", nested.Code);
        await new HarnessSessionArtifactStore(fixture.Options).MarkSessionDeletedAsync(root.Run.SessionId.Value, HarnessStoreTestFixture.Timestamp, default);
        var deleted = await Assert.ThrowsAsync<HarnessStoreException>(() => reader.ReadAsync(root.Run.RunId, default));
        Assert.Equal("subagent_root_not_found", deleted.Code);
    }
}
