extern alias Broker;

using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Subagents;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>Detached children (ADR 0045): opt-in provenance and the Host lifecycle around a root's end.</summary>
public sealed class SubagentDetachedLaunchTests
{
    private const string Prompt = "Review the parser, and keep that review running in the background after you answer.";

    [Theory]
    [InlineData("keep that review running in the background", null)]
    [InlineData("KEEP that review   running, in the background!", null)]
    [InlineData("run it in the background", "subagent_detached_user_request_required")]
    [InlineData("the", "subagent_detached_user_request_required")]
    [InlineData("background", "subagent_detached_user_request_required")]
    [InlineData("review run", "subagent_detached_user_request_required")]
    [InlineData("in the back", "subagent_detached_user_request_required")]
    public async Task A_detached_spawn_needs_the_users_own_words_before_any_workspace_or_child(string quote, string? error)
    {
        await using var setup = await Setup.CreateAsync();
        var request = new SubagentSpawnRequest("review", "Review the parser", Detached: true, DetachedRequest: quote);

        if (error is not null)
        {
            var refused = await Assert.ThrowsAsync<SubagentControlRejectedException>(() =>
                setup.Factory.CreateAsync(setup.Root, "spawn_detached", request, default));
            Assert.Equal(error, refused.Code);
            Assert.Equal(0, setup.Models.Calls);
            Assert.Equal(0, setup.Workspaces.Calls);
            Assert.Equal(0, setup.Gateways.Retained);
            Assert.Empty(await setup.Agents.ListAsync(setup.Root, default));
            return;
        }

        var child = await setup.Factory.CreateAsync(setup.Root, "spawn_detached", request, default);
        Assert.True(child.Detached);
        Assert.Equal(1, setup.Gateways.Retained);
        Assert.True(Assert.Single(await setup.Agents.ListAsync(setup.Root, default)).Detached);
    }

    [Fact]
    public async Task A_detached_spawn_without_a_broker_seam_is_refused_by_name()
    {
        await using var setup = await Setup.CreateAsync(withGateways: false);
        var refused = await Assert.ThrowsAsync<SubagentControlRejectedException>(() => setup.Factory.CreateAsync(setup.Root,
            "spawn_detached", new("review", "Review the parser", Detached: true,
                DetachedRequest: "keep that review running in the background"), default));
        Assert.Equal("subagent_detached_unavailable", refused.Code);
        Assert.Empty(await setup.Agents.ListAsync(setup.Root, default));
    }

    [Fact]
    public async Task A_normal_root_end_stops_attached_children_and_an_explicit_cancel_stops_detached_ones()
    {
        await using var setup = await Setup.CreateAsync();
        var attached = await setup.Factory.CreateAsync(setup.Root, "spawn_attached", new("review", "Check tests"), default);
        var detached = await setup.Factory.CreateAsync(setup.Root, "spawn_detached", new("review", "Review the parser",
            Detached: true, DetachedRequest: "keep that review running in the background"), default);
        using var scheduler = new SubagentScheduler(setup.Storage.EventStore, setup.Agents, setup.Scopes, setup.Roots, setup.Clock);
        var cleanup = new Cleanup(setup.Roots);
        await scheduler.TickAsync(cleanup, default);
        Assert.Equal(HarnessRunState.Running, await setup.StateAsync(attached.AgentId));
        Assert.Equal(HarnessRunState.Running, await setup.StateAsync(detached.AgentId));

        await setup.EndRootAsync();
        await scheduler.CancelChildrenBeforeReleaseAsync(setup.Root, cleanup, default);
        await scheduler.TickAsync(cleanup, default);

        Assert.Equal(HarnessRunState.Cancelled, await setup.StateAsync(attached.AgentId));
        Assert.Equal(HarnessRunState.Running, await setup.StateAsync(detached.AgentId));
        Assert.False(await setup.Agents.IsFamilyCanceledAsync(setup.Root, default));
        var survivor = Assert.Single(await setup.Agents.ListAsync(setup.Root, default), a => a.Detached);
        Assert.False(survivor.Canceled);

        await setup.Agents.CancelFamilyAsync(setup.Root, default);
        await scheduler.TickAsync(cleanup, default);

        var stopped = (await setup.Agents.GetLatestAttemptAsync(setup.Root, detached.AgentId, default))!.Run;
        Assert.Equal(HarnessRunState.Cancelled, stopped.State);
        Assert.Equal("subagent_parent_canceled", stopped.TerminalErrorCode);
    }

    [Fact]
    public async Task A_cancelled_root_stops_its_detached_children_even_without_a_family_mark()
    {
        await using var setup = await Setup.CreateAsync();
        var detached = await setup.Factory.CreateAsync(setup.Root, "spawn_detached", new("review", "Review the parser",
            Detached: true, DetachedRequest: "keep that review running in the background"), default);
        using var scheduler = new SubagentScheduler(setup.Storage.EventStore, setup.Agents, setup.Scopes, setup.Roots, setup.Clock);
        var cleanup = new Cleanup(setup.Roots);
        await scheduler.TickAsync(cleanup, default);
        Assert.Equal(HarnessRunState.Running, await setup.StateAsync(detached.AgentId));

        await setup.EndRootAsync(HarnessRunState.Cancelled);
        await scheduler.TickAsync(cleanup, default);

        Assert.Equal(HarnessRunState.Cancelled, await setup.StateAsync(detached.AgentId));
        Assert.True(await setup.Agents.IsFamilyCanceledAsync(setup.Root, default));
    }

    [Fact]
    public async Task A_detached_child_queued_when_its_root_ends_is_still_admitted()
    {
        await using var setup = await Setup.CreateAsync();
        var detached = await setup.Factory.CreateAsync(setup.Root, "spawn_detached", new("review", "Review the parser",
            Detached: true, DetachedRequest: "keep that review running in the background"), default);
        await setup.EndRootAsync();
        setup.Roots.Remove(setup.Root);
        using var scheduler = new SubagentScheduler(setup.Storage.EventStore, setup.Agents, setup.Scopes, setup.Roots, setup.Clock);

        await scheduler.TickAsync(new Cleanup(setup.Roots), default);

        var attempt = (await setup.Agents.GetLatestAttemptAsync(setup.Root, detached.AgentId, default))!;
        Assert.Equal(HarnessRunState.Running, attempt.Run.State);
        Assert.True(setup.Roots.TryGetRoot(attempt.Run.RunId, out _));
    }

    private sealed class Setup : IAsyncDisposable
    {
        public required BackendHarnessTestFixture Storage { get; init; }
        public required RunId Root { get; init; }
        public required SqliteSubagentStore Agents { get; init; }
        public required SqliteSubagentScopeStore Scopes { get; init; }
        public required AgentRuntimeRootRegistry Roots { get; init; }
        public required SubagentRunFactory Factory { get; init; }
        public required Selection Models { get; init; }
        public required Workspaces Workspaces { get; init; }
        public required Gateways Gateways { get; init; }
        public required Clock Clock { get; init; }

        public static async Task<Setup> CreateAsync(bool withGateways = true)
        {
            var storage = await BackendHarnessTestFixture.CreateAsync();
            var bytes = Encoding.UTF8.GetBytes(Prompt);
            var root = await storage.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
                "run_" + new string('a', 32), new string('a', 32), storage.RootPath,
                [new(new("payload_" + new string('e', 32)), HarnessPayloadKind.UserPrompt,
                    HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                    HarnessPayloadEncoding.Utf8Text, bytes.Length, bytes)]);
            var identity = await new WindowsWorkspaceIdentityResolver().ResolveAsync(storage.RootPath, default);
            var policy = PolicySnapshotCanonicalizer.Create(1, root.Run.Preset, identity, [], BackendHarnessTestFixture.Timestamp);
            var roots = new AgentRuntimeRootRegistry();
            roots.Register(root.Run.RunId, storage.RootPath, policy);
            var agents = new SqliteSubagentStore(storage.Options, storage.PayloadStore);
            var scopes = new SqliteSubagentScopeStore(storage.Options, new WorkspaceJournalProtector(storage.KeyStore));
            var models = new Selection();
            var workspaces = new Workspaces();
            var gateways = new Gateways();
            var clock = new Clock(DateTimeOffset.UtcNow);
            var factory = new SubagentRunFactory(storage.EventStore, agents, new SqliteAgentRunProviderSelectionStore(storage.Options),
                storage.PayloadStore, scopes, roots, models, workspaces, clock, withGateways ? gateways : null);
            return new()
            {
                Storage = storage, Root = root.Run.RunId, Agents = agents, Scopes = scopes, Roots = roots, Factory = factory,
                Models = models, Workspaces = workspaces, Gateways = gateways, Clock = clock,
            };
        }

        public async Task<HarnessRunState> StateAsync(string agentId) =>
            (await Agents.GetLatestAttemptAsync(Root, agentId, default))!.Run.State;

        public async Task EndRootAsync(HarnessRunState state = HarnessRunState.Completed)
        {
            await using var connection = await Storage.Options.OpenConnectionAsync(default);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_runs SET state=$state, terminal_at_utc=updated_at_utc WHERE run_id=$run";
            command.Parameters.AddWithValue("$state", (int)state);
            command.Parameters.AddWithValue("$run", Root.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        public async ValueTask DisposeAsync()
        {
            Factory.Dispose();
            await Storage.DisposeAsync();
        }
    }

    private sealed class Selection : ISubagentModelSelectionProvider
    {
        public int Calls;
        public AgentRunProviderSelection Pin { get; } = new("Custom OpenAI", "custom", "chat_completions",
            "https://example.invalid/v1/", "fixture-model", null, "{}", null, "fixture-model", "native");
        public Task<AgentRunProviderSelection> ResolveAsync(RunId rootRunId, SubagentRole role, CancellationToken ct, string? model = null, string? providerId = null)
        { Calls++; return Task.FromResult(Pin); }
    }

    private sealed class Workspaces : ISubagentWorkspaceFactory
    {
        public int Calls;
        public Task<PreparedSubagentWorkspace> CreateAsync(AgentRun parent, string root, PolicySnapshot policy,
            string agentId, SubagentRole role, CancellationToken ct)
        { Calls++; return Task.FromResult(new PreparedSubagentWorkspace(root, policy, null)); }
    }

    private sealed class Gateways : IToolGatewayFactory
    {
        public int Retained;
        public bool IsConfigured => true;
        public Task RetainDetachedParentAsync(RunId parentRunId, CancellationToken cancellationToken)
        { Retained++; return Task.CompletedTask; }
        public Task<IToolGateway> CreateAsync(string workspaceRoot, PolicySnapshot policySnapshot,
            RunId runId, string? agentSessionId, CancellationToken cancellationToken) =>
            Task.FromException<IToolGateway>(new InvalidOperationException("test_gateway_unavailable"));
        public Task<System.Text.Json.Nodes.JsonObject> ListSessionTasksAsync(string runId, RuAgent.Protocol.SessionTaskRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<System.Text.Json.Nodes.JsonObject>(new InvalidOperationException("test_gateway_unavailable"));
        public Task<(bool Authorized, string? AuthorizationToken, Broker::RuAgent.Broker.Policy.ApprovalRememberPlan? RememberPlan)> RespondApprovalAsync(string runId, string hostApprovalId,
            string command, CancellationToken cancellationToken) =>
            Task.FromException<(bool Authorized, string? AuthorizationToken, Broker::RuAgent.Broker.Policy.ApprovalRememberPlan? RememberPlan)>(new InvalidOperationException("test_gateway_unavailable"));
        public Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseRunAsync(string runId) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Cleanup(AgentRuntimeRootRegistry roots) : IAgentRuntimeRunCleanup
    {
        public Task CancelAndRemoveRunAsync(RunId runId, CancellationToken ct)
        { roots.Remove(runId); return Task.CompletedTask; }
    }

    private sealed class Clock(DateTimeOffset now) : ITimeProvider
    { public DateTimeOffset UtcNow { get; set; } = now; }
}
