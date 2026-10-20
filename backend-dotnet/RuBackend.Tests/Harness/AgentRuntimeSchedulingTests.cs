using Microsoft.Extensions.Logging.Abstractions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuBackend.Business;
using RuBackend.Business.Sessions;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentRuntimeSchedulingTests
{
    [Fact]
    public async Task Two_sessions_are_driven_simultaneously_in_the_same_workspace_with_default_membership_policy()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        string workspaceRoot = Path.Combine(fixture.RootPath, "shared-workspace");
        var first = await fixture.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), workspaceRoot);
        var second = await fixture.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), workspaceRoot);
        Assert.Equal(first.Run.WorkspaceId, second.Run.WorkspaceId);
        var roots = new AgentRuntimeRootRegistry();
        roots.Register(first.Run.RunId, workspaceRoot);
        roots.Register(second.Run.RunId, workspaceRoot);
        var factory = new BlockingFactory(null);
        var sessions = new SessionStore(new DataPaths(Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"), Path.Combine(fixture.RootPath, "activity.log")), TimeProvider.System);
        var dispatcher = new AgentConversationOutboxDispatcher(new HarnessOutboxStore(fixture.Options), fixture.PayloadStore,
            sessions, new HarnessModelContextBootstrapper(sessions, fixture.PayloadStore, new CryptographicIdGenerator(), TimeProvider.System),
            new AgentRunHistoryTransitionSink(factory, roots), TimeProvider.System);
        using var loop = new AgentRuntimeLoopService(dispatcher, fixture.EventStore, factory, roots,
            NullLogger<AgentRuntimeLoopService>.Instance,
            workspaceCoordinatorStore: new WorkspaceCoordinatorStore(fixture.Options),
            workspaceJournalProtector: new WorkspaceJournalProtector(fixture.KeyStore),
            brokerGateways: new UnavailableToolGatewayFactory());
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await factory.TwoEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, factory.Calls);
            Assert.Equal(0, factory.Cancelled);
        }
        finally { await loop.StopAsync(CancellationToken.None); }
        Assert.Equal(2, factory.Cancelled);
    }

    [Fact]
    public async Task New_registration_starts_while_an_older_run_is_blocked()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var first = await fixture.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running), "run_" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var roots = new AgentRuntimeRootRegistry();
        roots.Register(first.Run.RunId, fixture.RootPath);
        var factory = new BlockingFactory(first.Run.RunId);
        var sessions = new SessionStore(new DataPaths(Path.Combine(fixture.RootPath, "settings.json"), Path.Combine(fixture.RootPath, "sessions"), Path.Combine(fixture.RootPath, "activity.log")), TimeProvider.System);
        var dispatcher = new AgentConversationOutboxDispatcher(new HarnessOutboxStore(fixture.Options), fixture.PayloadStore, sessions,
            new HarnessModelContextBootstrapper(sessions, fixture.PayloadStore, new CryptographicIdGenerator(), TimeProvider.System),
            new AgentRunHistoryTransitionSink(factory, roots), TimeProvider.System);
        using var loop = new AgentRuntimeLoopService(dispatcher, fixture.EventStore, factory, roots, NullLogger<AgentRuntimeLoopService>.Instance,
            new WorkspaceCoordinatorStore(fixture.Options), new WorkspaceJournalProtector(fixture.KeyStore), new UnavailableToolGatewayFactory());
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await factory.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await loop.StepOnceAsync(CancellationToken.None, waitForWorkers: false);
            await loop.StepOnceAsync(CancellationToken.None, waitForWorkers: false);
            Assert.Equal(1, factory.Calls);
            var second = await fixture.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running), "run_" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            roots.Register(second.Run.RunId, fixture.RootPath);
            await Task.WhenAny(factory.SecondEntered.Task, Task.Delay(2000));
            Assert.True(factory.SecondEntered.Task.IsCompleted, "There are three free slots, but the new run never gets scheduled.");
        }
        finally { await loop.StopAsync(CancellationToken.None); }
        Assert.Equal(1, factory.Cancelled);
    }

    [Fact]
    public async Task Worker_limit_and_shutdown_cover_queued_runs()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var roots = new AgentRuntimeRootRegistry();
        for (var i = 0; i < 6; i++)
        {
            var run = await fixture.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
                "run_" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            roots.Register(run.Run.RunId, fixture.RootPath);
        }
        var factory = new BlockingFactory(null);
        var sessions = new SessionStore(new DataPaths(Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"), Path.Combine(fixture.RootPath, "activity.log")), TimeProvider.System);
        var dispatcher = new AgentConversationOutboxDispatcher(new HarnessOutboxStore(fixture.Options), fixture.PayloadStore,
            sessions, new HarnessModelContextBootstrapper(sessions, fixture.PayloadStore, new CryptographicIdGenerator(), TimeProvider.System),
            new AgentRunHistoryTransitionSink(factory, roots), TimeProvider.System);
        using var loop = new AgentRuntimeLoopService(dispatcher, fixture.EventStore, factory, roots, NullLogger<AgentRuntimeLoopService>.Instance,
            new WorkspaceCoordinatorStore(fixture.Options), new WorkspaceJournalProtector(fixture.KeyStore), new UnavailableToolGatewayFactory());
        await loop.StartAsync(CancellationToken.None);
        try
        {
            await factory.FourEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await loop.StepOnceAsync(CancellationToken.None, waitForWorkers: false);
            Assert.Equal(4, factory.Calls);
        }
        finally { await loop.StopAsync(CancellationToken.None); }
        Assert.Equal(4, factory.Calls);
        Assert.Equal(4, factory.Cancelled);
    }

    private sealed class BlockingFactory(RunId? first) : IAgentRuntimeCoordinatorFactory
    {
        public int Calls;
        public int Cancelled;
        public TaskCompletionSource<bool> FourEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> TwoEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgentRunCoordinator> CreateAsync(string workspaceRoot, PolicySnapshot? policySnapshot = null, RunId? runId = null, string? agentSessionId = null, CancellationToken cancellationToken = default)        {
            int count = Interlocked.Increment(ref Calls);
            if (count == 4) FourEntered.TrySetResult(true);
            if (count == 2) TwoEntered.TrySetResult(true);
            if (first is null || runId == first)
            {
                FirstEntered.TrySetResult(true);
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                finally { Interlocked.Increment(ref Cancelled); }
            }
            else SecondEntered.TrySetResult(true);
            throw new NotSupportedException();
        }
        public void DropRetainedApproval(RunId runId) { }
    }
}
