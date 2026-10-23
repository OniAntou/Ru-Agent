using System.Net;
using RuAgent.Cli;
using RuAgent.Cli.TerminalGui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class StopDuringCreateTests
{
    [Fact]
    public async Task Stop_keeps_the_chat_session_alive_until_an_accepted_run_is_cancelled()
    {
        var enteredCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelPath = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownSettlementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new DelayedCreateHandler(enteredCreate, releaseCreate, enteredCancel, releaseCancel, cancelPath);
        using var http = new HttpClient(handler);
        var descriptor = new RuntimeDescriptor(1, "http://127.0.0.1:1/", "synthetic-token", 1, "start", "nonce", "now");
        using var client = new AgentApiClient(
            new PublicHostSession(new Uri("http://127.0.0.1:1/"), "synthetic-token", descriptor), http);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        using var runSession = new ChatRunSession();
        loop.SetShutdownSettlement(runSession.WaitForRunSettlementAsync);
        var sessionReleased = ReleaseSessionAfterShutdownAsync();
        var request = new CreateAgentRunRequest("session", "C:/workspace", "slow create", "standard", null, "probe-idempotency");

        loop.SetPromptHandler(async (_, _, turnToken) =>
        {
            runSession.BeginNewTurn(turnToken);
            using var settlement = runSession.RetainRunSettlement();
            try
            {
                var created = await client.CreateRunAsync(request, runSession.Token);
                runSession.AcceptCreatedRun(created.RunId);
                if (runSession.InterruptedRunId is { } interruptedRunId)
                    await client.CancelAsync(interruptedRunId, CancellationToken.None);
            }
            finally
            {
                runSession.EndTurn();
            }
        });

        window.Composer.Input.Text = "slow create";
        loop.Submit();
        await enteredCreate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        loop.Stop();
        await loop.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdownSettlementEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ownerStayedAliveAfterUiDrain = !sessionReleased.IsCompleted;
        releaseCreate.TrySetResult();
        var cancelStarted = await Task.WhenAny(enteredCancel.Task, Task.Delay(TimeSpan.FromSeconds(5))) == enteredCancel.Task;
        var ownerStayedAliveForCancelAck = !sessionReleased.IsCompleted;
        releaseCancel.TrySetResult();
        await sessionReleased.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(ownerStayedAliveAfterUiDrain,
            "The ChatSession must retain run identity after the UI completion bound while create reconciliation is pending.");
        Assert.True(cancelStarted, "A late accepted run must be cancelled before its owner is released.");
        Assert.True(ownerStayedAliveForCancelAck,
            "The ChatSession must retain run identity until the exact run-cancel request settles.");
        Assert.Equal("/agent/v1/runs/run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/cancel", await cancelPath.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(runSession.InterruptedRunId);

        async Task ReleaseSessionAfterShutdownAsync()
        {
            await loop.Completion.ConfigureAwait(false);
            shutdownSettlementEntered.TrySetResult();
            await loop.WaitForShutdownSettlementAsync().ConfigureAwait(false);
            runSession.Dispose();
        }
    }

    private sealed class DelayedCreateHandler(
        TaskCompletionSource enteredCreate,
        TaskCompletionSource releaseCreate,
        TaskCompletionSource enteredCancel,
        TaskCompletionSource releaseCancel,
        TaskCompletionSource<string> cancelPath) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/agent/v1/runs")
            {
                enteredCreate.TrySetResult();
                await releaseCreate.Task.ConfigureAwait(false); // The client intentionally reconciles past caller cancellation.
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""
                        {"run_id":"run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","session_id":"session","workspace_id":"workspace","status":"running","preset":"standard","created_at":"2026-10-23T00:00:00Z","updated_at":"2026-10-23T00:00:00Z","policy_snapshot_version":1,"policy_snapshot_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","version":1,"last_sequence":1}
                        """)
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri is { } cancelUri)
            {
                cancelPath.TrySetResult(cancelUri.AbsolutePath);
                enteredCancel.TrySetResult();
                await releaseCancel.Task.ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            }

            throw new InvalidOperationException($"Unexpected HTTP request: {request.Method} {request.RequestUri}");
        }
    }
}
