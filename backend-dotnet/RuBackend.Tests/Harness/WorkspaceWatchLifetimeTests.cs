using System.Text.Json;
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

public sealed class WorkspaceWatchLifetimeTests
{
    [Fact]
    public async Task Ending_one_drive_preserves_the_other_drives_baseline_and_durable_journal()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var root = Path.Combine(fixture.RootPath, "workspace");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "app.txt");
        await File.WriteAllTextAsync(path, "original");
        var first = await fixture.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), root);
        var second = await fixture.SeedRunWithEncryptedWorkspaceAsync(new(HarnessRunState.Running),
            "run_" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), root);
        var roots = new AgentRuntimeRootRegistry();
        roots.Register(first.Run.RunId, root);
        var factory = new ControlledFactory(first.Run.RunId);
        var sessions = new SessionStore(new DataPaths(Path.Combine(fixture.RootPath, "settings.json"),
            Path.Combine(fixture.RootPath, "sessions"), Path.Combine(fixture.RootPath, "activity.log")), TimeProvider.System);
        var dispatcher = new AgentConversationOutboxDispatcher(new HarnessOutboxStore(fixture.Options), fixture.PayloadStore,
            sessions, new HarnessModelContextBootstrapper(sessions, fixture.PayloadStore, new CryptographicIdGenerator(), TimeProvider.System),
            new AgentRunHistoryTransitionSink(factory, roots), TimeProvider.System);
        var monitor = new WorkspaceExternalChangeMonitor();
        var store = new WorkspaceCoordinatorStore(fixture.Options);
        var protector = new WorkspaceJournalProtector(fixture.KeyStore);
        using var loop = new AgentRuntimeLoopService(dispatcher, fixture.EventStore, factory, roots,
            NullLogger<AgentRuntimeLoopService>.Instance, workspaceChangeMonitor: monitor,
            workspaceCoordinatorStore: store, workspaceJournalProtector: protector,
            brokerGateways: new UnavailableToolGatewayFactory());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await loop.StartAsync(CancellationToken.None);
        var firstDrive = loop.StepOnceAsync(cancellation.Token);
        try
        {
            await factory.FirstEntered.Task.WaitAsync(cancellation.Token);
            roots.Register(second.Run.RunId, root);
            await loop.StepOnceAsync(cancellation.Token, waitForWorkers: false);
            await factory.SecondEntered.Task.WaitAsync(cancellation.Token);
            factory.ReleaseFirst.TrySetResult(true);
            // This iteration only snapshotted the first worker. Its completion
            // includes finally cleanup while the second worker stays active.
            await firstDrive.WaitAsync(cancellation.Token);
            Assert.True(monitor.TryGetExpectedContent(root, "app.txt", out var original),
                "A peer drive removed the still-active workspace baseline.");
            Assert.Equal("original", System.Text.Encoding.UTF8.GetString(original!));

            await File.WriteAllTextAsync(path, "external edit", cancellation.Token);
            IReadOnlyList<WorkspaceChangeRecord> changes;
            do
            {
                changes = await store.ListChangesAsync(first.Run.WorkspaceId, 0, 100, cancellation.Token);
                if (changes.Count == 0) await Task.Delay(20, cancellation.Token);
            } while (changes.Count == 0);
            var change = Assert.Single(changes);
            Assert.Equal("app.txt", change.NormalizedPath);
            var envelope = await store.GetChangeEnvelopeAsync(first.Run.WorkspaceId, change.ChangeId, cancellation.Token);
            Assert.NotNull(envelope);
            var decrypted = await protector.DecryptAsync(first.Run.WorkspaceId, change.ChangeId,
                change.NormalizedPath, envelope.Journal, cancellation.Token);
            using var payload = JsonDocument.Parse(decrypted);
            Assert.Equal("unknown_external", payload.RootElement.GetProperty("writer_kind").GetString());
            Assert.Equal("original", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
                payload.RootElement.GetProperty("content_base64").GetString()!)));
        }
        finally
        {
            cancellation.Cancel();
            factory.ReleaseFirst.TrySetResult(true);
            try { await firstDrive; } catch (OperationCanceledException) { }
            // Await the worker tasks, including drive cleanup, before disposing
            // the fixture's database and files.
            await loop.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ControlledFactory(RunId first) : IAgentRuntimeCoordinatorFactory
    {
        public TaskCompletionSource<bool> FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentRunCoordinator> CreateAsync(string workspaceRoot, PolicySnapshot? policySnapshot = null,
            RunId? runId = null, string? agentSessionId = null, CancellationToken cancellationToken = default)
        {
            if (runId == first)
            {
                FirstEntered.TrySetResult(true);
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("controlled_gateway_failure");
            }
            SecondEntered.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new NotSupportedException();
        }

        public void DropRetainedApproval(RunId runId) { }
    }
}
