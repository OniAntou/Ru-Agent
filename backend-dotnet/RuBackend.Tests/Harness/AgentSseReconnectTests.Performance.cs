using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Protocol.Harness;
using RuBackend.Harness;
using Xunit;
using Xunit.Abstractions;

namespace RuBackend.Tests.Harness;

public sealed partial class AgentSseReconnectTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Text_wakes_do_not_each_reload_the_run_and_durable_tail()
    {
        using var tmp = TempWorkspace.Create();
        var store = new StreamProbeStore(1);
        using var factory = ProbeFactory(tmp, store);
        using var client = factory.CreateAuthenticatedClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync($"/agent/v1/runs/{StreamProbeStore.Id}/events",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Contains("id: 1", await ReadProbeFrameAsync(reader, timeout.Token));
        var hub = factory.Services.GetRequiredService<HarnessLiveEventHub>();
        var before = store.RunReads;
        var clock = Stopwatch.StartNew();
        for (var index = 0; index < 80; index++)
        {
            hub.TryPublish(StreamProbeStore.Run, "assistant.delta", $"piece-{index}");
            Assert.Contains($"piece-{index}", await ReadProbeFrameAsync(reader, timeout.Token));
        }

        // Scale the poll allowance with actual elapsed time, including loaded CI.
        var allowance = 3 + (int)Math.Ceiling(clock.Elapsed.TotalMilliseconds / 100);
        output.WriteLine($"80 text frames: {store.RunReads - before} run reloads, {store.EventReads} durable reads, {clock.Elapsed.TotalMilliseconds:F1} ms");
        Assert.InRange(store.RunReads - before, 0, allowance);
        Assert.InRange(store.EventReads, 1, 2);
        timeout.Cancel();
    }

    [Fact]
    public async Task Replay_closes_store_readers_before_network_writes_and_batches_output()
    {
        using var tmp = TempWorkspace.Create();
        var store = new StreamProbeStore(200);
        var probe = new StreamOutputProbe(store);
        using var factory = ProbeFactory(tmp, store, probe);
        using var client = factory.CreateAuthenticatedClient();
        using var response = await client.GetAsync($"/agent/v1/runs/{StreamProbeStore.Id}/events?live=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        output.WriteLine($"200 durable frames: {store.EventReads} read batches, {probe.Writes} network writes, reader active at write: {probe.WroteWhileReaderActive}");

        Assert.Equal(200, body.Split('\n').Count(line => line.StartsWith("id: ", StringComparison.Ordinal)));
        Assert.Contains("id: 200\n", body);
        Assert.False(probe.WroteWhileReaderActive);
        Assert.InRange(probe.Writes, 1, 64);
        Assert.Equal(0, store.ActiveReaders);
    }

    [Fact]
    public async Task An_activity_boundary_replays_new_durables_before_its_queued_text()
    {
        using var tmp = TempWorkspace.Create();
        var store = new StreamProbeStore(1);
        using var factory = ProbeFactory(tmp, store);
        using var client = factory.CreateAuthenticatedClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync($"/agent/v1/runs/{StreamProbeStore.Id}/events",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Contains("id: 1", await ReadProbeFrameAsync(reader, timeout.Token));
        var hub = factory.Services.GetRequiredService<HarnessLiveEventHub>();

        store.Advance("model.step_started");
        hub.TryPublish(StreamProbeStore.Run, "agent.activity", "activity-boundary");
        hub.TryPublish(StreamProbeStore.Run, "assistant.delta", "text-after-boundary");

        Assert.Contains("id: 2", await ReadProbeFrameAsync(reader, timeout.Token));
        Assert.Contains("activity-boundary", await ReadProbeFrameAsync(reader, timeout.Token));
        Assert.Contains("text-after-boundary", await ReadProbeFrameAsync(reader, timeout.Token));
        timeout.Cancel();
    }

    [Fact]
    public async Task Terminal_transition_drains_multiple_queued_batches_in_publication_order()
    {
        using var tmp = TempWorkspace.Create();
        var store = new StreamProbeStore(1);
        using var factory = ProbeFactory(tmp, store);
        using var client = factory.CreateAuthenticatedClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await client.GetAsync($"/agent/v1/runs/{StreamProbeStore.Id}/events",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Contains("id: 1", await ReadProbeFrameAsync(reader, timeout.Token));
        var hub = factory.Services.GetRequiredService<HarnessLiveEventHub>();
        var expected = new List<string>();
        for (var index = 0; index < 60; index++)
        {
            var text = $"queued-text-{index:D3}";
            expected.Add(text);
            hub.TryPublish(StreamProbeStore.Run, "assistant.delta", text);
            if (index % 6 != 0) continue;
            var activity = $"queued-activity-{index:D3}";
            expected.Add(activity);
            hub.TryPublish(StreamProbeStore.Run, "agent.activity", activity);
        }
        store.Advance("run.state_changed", terminal: true);
        hub.NotifyRunChanged(StreamProbeStore.Run);
        var received = new List<string>();
        string last = string.Empty;
        while (await ReadProbeFrameAsync(reader, timeout.Token) is { Length: > 0 } frame)
        {
            if (frame.Contains("queued-", StringComparison.Ordinal))
                received.Add(Assert.Single(expected, marker => frame.Contains(marker, StringComparison.Ordinal)));
            last = frame;
        }
        Assert.Equal(expected, received);
        Assert.Contains("event: final_snapshot", last);
        Assert.Contains("completed", last);
        Assert.Equal(0, hub.SubscriberCount);
    }

    [Fact]
    public async Task Continuous_text_does_not_postpone_the_fallback_state_check()
    {
        using var tmp = TempWorkspace.Create();
        var store = new StreamProbeStore(1);
        using var factory = ProbeFactory(tmp, store);
        using var client = factory.CreateAuthenticatedClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var producerStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var response = await client.GetAsync($"/agent/v1/runs/{StreamProbeStore.Id}/events",
            HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Contains("id: 1", await ReadProbeFrameAsync(reader, timeout.Token));
        var hub = factory.Services.GetRequiredService<HarnessLiveEventHub>();
        store.Advance("run.state_changed", terminal: true);
        // No changed/activity signal: text must not reset the independent poll clock.
        var producer = Task.Run(async () =>
        {
            while (!producerStop.IsCancellationRequested)
            {
                hub.TryPublish(StreamProbeStore.Run, "assistant.delta", "continuous-text");
                await Task.Delay(1, producerStop.Token);
            }
        });
        try
        {
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string last = string.Empty;
            while (await ReadProbeFrameAsync(reader, finish.Token) is { Length: > 0 } frame) last = frame;
            Assert.Contains("event: final_snapshot", last);
            Assert.Contains("completed", last);
        }
        finally
        {
            producerStop.Cancel();
            try { await producer; } catch (OperationCanceledException) { }
        }
    }

    private static BackendWebApplicationFactory ProbeFactory(TempWorkspace tmp, StreamProbeStore store,
        StreamOutputProbe? probe = null) => new(dataPaths: tmp.DataPaths, configureServices: services =>
    {
        services.RemoveAll<IHarnessEventStore>();
        services.AddSingleton<IHarnessEventStore>(store);
        if (probe is not null) services.AddSingleton<IStartupFilter>(probe);
    });

    private static async Task<string> ReadProbeFrameAsync(StreamReader reader, CancellationToken ct)
    {
        var frame = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 && frame.Length > 0) return frame.ToString();
            frame.AppendLine(line);
        }
        return frame.ToString();
    }

    private sealed class StreamProbeStore : IHarnessEventStore
    {
        internal const string Id = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        internal static readonly RunId Run = new(Id);
        private readonly object _gate = new();
        private readonly List<HarnessEventEnvelope> _events = [];
        private AgentRun _run;
        internal int RunReads;
        internal int EventReads;
        internal int ActiveReaders;

        internal StreamProbeStore(int events)
        {
            var now = DateTimeOffset.UtcNow;
            _run = new(Run, new SessionId(new string('a', 32)), new WorkspaceId("ws_" + new string('b', 32)),
                new(HarnessRunState.Running), 1, events, 1, 0, 0, AgentPermissionPreset.Standard,
                1, new string('c', 64), now, now);
            for (var sequence = 1; sequence <= events; sequence++)
                _events.Add(new(1, new(new EventId("evt_" + Guid.NewGuid().ToString("N")), Run, sequence,
                    sequence == 1 ? "run.state_changed" : "model.step_started", now)));
        }

        public Task<AgentRun?> GetRunAsync(RunId run, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref RunReads);
            lock (_gate) return Task.FromResult<AgentRun?>(run == Run ? _run : null);
        }

        internal void Advance(string kind, bool terminal = false)
        {
            lock (_gate)
            {
                var previous = _run;
                var now = DateTimeOffset.UtcNow;
                var sequence = previous.LastSequence + 1;
                _events.Add(new(1, new(new EventId("evt_" + Guid.NewGuid().ToString("N")), Run, sequence, kind, now)));
                _run = new(previous.RunId, previous.SessionId, previous.WorkspaceId,
                    terminal ? new(HarnessRunState.Completed) : previous.Status, previous.Version + 1, sequence,
                    previous.BudgetWindow, previous.ModelStepsInWindow, previous.ToolCallsInWindow,
                    previous.Preset, previous.PolicySnapshotVersion, previous.PolicySnapshotSha256,
                    previous.CreatedAt, now, terminalAt: terminal ? now : null,
                    permissionProfileVersion: previous.PermissionProfileVersion);
            }
        }

        public async IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(RunId run, long after,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Interlocked.Increment(ref EventReads);
            Interlocked.Increment(ref ActiveReaders);
            try
            {
                HarnessEventEnvelope[] page;
                lock (_gate) page = _events.Where(item => item.Event.Sequence > after).ToArray();
                await Task.Yield();
                foreach (var item in page) { ct.ThrowIfCancellationRequested(); yield return item; }
            }
            finally { Interlocked.Decrement(ref ActiveReaders); }
        }

        public Task<CreateRunResult> TryCreateRunAsync(NewAgentRun request, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<HarnessCommitResult> CommitAsync(HarnessCommit commit, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class StreamOutputProbe(StreamProbeStore store) : IStartupFilter
    {
        internal int Writes;
        internal bool WroteWhileReaderActive;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                if (!context.Request.Path.Value!.EndsWith("/events", StringComparison.Ordinal))
                {
                    await proceed();
                    return;
                }
                var original = context.Response.Body;
                context.Response.Body = new ObservedStream(original, this);
                try { await proceed(); }
                finally { context.Response.Body = original; }
            });
            next(app);
        };

        private void Observe()
        {
            Interlocked.Increment(ref Writes);
            if (Volatile.Read(ref store.ActiveReaders) > 0) WroteWhileReaderActive = true;
        }

        private sealed class ObservedStream(Stream inner, StreamOutputProbe probe) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => inner.CanSeek;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => inner.Position = value; }
            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
            public override void SetLength(long value) => inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count)
            { probe.Observe(); inner.Write(buffer, offset, count); }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            { probe.Observe(); return inner.WriteAsync(buffer, offset, count, ct); }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            { probe.Observe(); return inner.WriteAsync(buffer, ct); }
        }
    }
}
