# Ru VTS Subtitle Voice-Isolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Execution status (2026-08-09):** Implemented in the current working tree and
automatically verified; live OmniVoice/VTS A/B remains pending. Tasks 2 and 3
were executed in one RED/GREEN cycle because changing the queue delegate is a
compile-time change required by `VoicePlaybackService`. No per-task commits were
created, so the commit checklist items below remain historical instructions.

**Goal:** Make VTS subtitles strictly fail-open so subtitle rendering, UI stalls, cancellation, or overload can never delay Ru voice synthesis or playback.

**Architecture:** Add one long-lived `VoiceSubtitleEventPump` with a bounded latest-state mailbox. `VoiceReplyAudioQueue` carries subtitle cues but only publishes them through the pump after the playback delegate reports that `MediaPlayer.Play()` has already been called; voice never invokes the presenter/UI or awaits subtitle work. Toggle-off avoids cue allocation and stops tracker work.

**Tech Stack:** C# 13, .NET 10, WinUI 3, `System.Threading.Channels`, `MediaPlayer`, xUnit.

---

## File map

- Create `gui-winui3/Services/VoiceSubtitleEventPump.cs`: bounded, non-blocking desired-state mailbox and isolated presenter worker.
- Create `gui-winui3.Tests/VoiceSubtitleEventPumpTests.cs`: pump overload, blocked subscriber, disabled path, and exception isolation.
- Modify `gui-winui3/Services/VoiceReplyAudioQueue.cs`: replace synchronous subtitle callbacks with a playback-start notifier plus pump `TryShow`/`TryHide`.
- Modify `gui-winui3.Tests/VoicePlaybackQueueTests.cs`: lock down playback-before-subtitle ordering and prove a blocked renderer cannot block audio.
- Modify `gui-winui3/Services/VoicePlaybackService.cs`: own/configure the pump, gate cue creation, publish clear non-blockingly, and notify only after `MediaPlayer.Play()`.
- Create `gui-winui3.Tests/VoicePlaybackSubtitleIsolationTests.cs`: toggle and cancellation isolation at the service seam.
- Modify `gui-winui3/MainWindow.xaml.cs`: configure the presenter through `VoicePlaybackService`.
- Modify `gui-winui3/Views/VoiceView.xaml.cs`: apply the persisted toggle to both the voice event pump and overlay tracker.
- Modify `README.md`: document the fail-open voice contract.
- Modify `docs/superpowers/specs/2026-08-09-ru-vts-subtitle-design.md`: mark implementation status and record final verification evidence.
- Delete `docs/superpowers/plans/2026-08-09-ru-vts-subtitle.md`: retain the existing deletion of the superseded karaoke plan.

### Task 1: Non-blocking subtitle event pump

**Files:**
- Create: `gui-winui3/Services/VoiceSubtitleEventPump.cs`
- Create: `gui-winui3.Tests/VoiceSubtitleEventPumpTests.cs`

- [ ] **Step 1: Write failing pump tests**

Create `gui-winui3.Tests/VoiceSubtitleEventPumpTests.cs`:

```csharp
using System.Threading;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoiceSubtitleEventPumpTests
{
    [Fact]
    public void DisabledPumpRejectsSubtitleWithoutChangingPresenter()
    {
        using var presenter = new SubtitlePresenter();
        using var pump = new VoiceSubtitleEventPump(presenter);

        Assert.False(pump.TryShow(new VoiceSubtitleCue("r1", "Cau mot")));
        Assert.Equal(SubtitleSnapshot.Empty, presenter.Current);
    }

    [Fact]
    public async Task TryShowReturnsWhilePresenterSubscriberIsBlocked()
    {
        using var presenter = new SubtitlePresenter();
        using var pump = new VoiceSubtitleEventPump(presenter);
        var rendererEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRenderer = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.SnapshotChanged += snapshot =>
        {
            if (snapshot != SubtitleSnapshot.Empty)
            {
                rendererEntered.TrySetResult(true);
                releaseRenderer.Task.GetAwaiter().GetResult();
            }
        };
        pump.SetEnabled(true);

        Assert.True(pump.TryShow(new VoiceSubtitleCue("r1", "Cau mot")));
        await rendererEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Task<bool> secondPublish = Task.Run(
            () => pump.TryShow(new VoiceSubtitleCue("r2", "Cau hai")));
        Assert.True(await secondPublish.WaitAsync(TimeSpan.FromSeconds(1)));

        releaseRenderer.TrySetResult(true);
    }

    [Fact]
    public async Task SubscriberFailureDoesNotStopLaterSubtitleStates()
    {
        using var presenter = new SubtitlePresenter();
        using var pump = new VoiceSubtitleEventPump(presenter);
        int calls = 0;
        var secondCall = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.SnapshotChanged += _ =>
        {
            int current = Interlocked.Increment(ref calls);
            if (current == 2)
            {
                secondCall.TrySetResult(true);
            }
            throw new InvalidOperationException("renderer failed");
        };
        pump.SetEnabled(true);

        Assert.True(pump.TryShow(new VoiceSubtitleCue("r1", "Cau mot")));
        while (Volatile.Read(ref calls) < 1)
        {
            await Task.Yield();
        }
        Assert.True(pump.TryShow(new VoiceSubtitleCue("r2", "Cau hai")));

        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, Volatile.Read(ref calls));
    }
}
```

- [ ] **Step 2: Run the tests and verify RED**

Run:

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~VoiceSubtitleEventPumpTests"
```

Expected: build fails with `CS0246` because `VoiceSubtitleEventPump` does not exist.

- [ ] **Step 3: Implement the minimal bounded event pump**

Create `gui-winui3/Services/VoiceSubtitleEventPump.cs`:

```csharp
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;

namespace RuApp.Services;

public sealed class VoiceSubtitleEventPump : IDisposable
{
    private readonly SubtitlePresenter _presenter;
    private readonly Channel<SubtitleUpdate> _mailbox;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _consumer;
    private string? _latestSegmentId;
    private int _enabled;
    private int _disposed;

    public VoiceSubtitleEventPump(SubtitlePresenter presenter)
    {
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _mailbox = Channel.CreateBounded<SubtitleUpdate>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        _consumer = Task.Run(() => ConsumeAsync(_cancellation.Token));
    }

    public bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Interlocked.Exchange(ref _enabled, enabled ? 1 : 0);
        if (enabled)
        {
            return;
        }

        Interlocked.Exchange(ref _latestSegmentId, null);
        while (_mailbox.Reader.TryRead(out _))
        {
        }
        _presenter.CancelAll();
    }

    public bool TryShow(VoiceSubtitleCue cue)
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
        Volatile.Write(ref _latestSegmentId, cue.Id);
        return _mailbox.Writer.TryWrite(SubtitleUpdate.Show(cue));
    }

    public bool TryHide(string segmentId)
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        string? current = Volatile.Read(ref _latestSegmentId);
        if (current is null ||
            !string.Equals(current, segmentId, StringComparison.Ordinal) ||
            !ReferenceEquals(Interlocked.CompareExchange(
                ref _latestSegmentId, null, current), current))
        {
            return false;
        }
        return _mailbox.Writer.TryWrite(SubtitleUpdate.Hide(segmentId));
    }

    public bool TryClear()
    {
        if (!IsEnabled || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
        Interlocked.Exchange(ref _latestSegmentId, null);
        return _mailbox.Writer.TryWrite(SubtitleUpdate.Clear());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Interlocked.Exchange(ref _enabled, 0);
        Interlocked.Exchange(ref _latestSegmentId, null);
        _mailbox.Writer.TryComplete();
        _cancellation.Cancel();
        _presenter.CancelAll();
        _cancellation.Dispose();
        _ = _consumer.Exception;
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (SubtitleUpdate update in
                _mailbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!IsEnabled || !IsCurrent(update))
                {
                    continue;
                }
                try
                {
                    switch (update.Kind)
                    {
                        case SubtitleUpdateKind.Show:
                            _presenter.BeginSegment(update.SegmentId, update.Text);
                            break;
                        case SubtitleUpdateKind.Hide:
                            _presenter.EndSegment(update.SegmentId);
                            break;
                        case SubtitleUpdateKind.Clear:
                            _presenter.CancelAll();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VoiceSubtitleEventPump] Presenter failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private bool IsCurrent(SubtitleUpdate update)
    {
        string? latest = Volatile.Read(ref _latestSegmentId);
        return update.Kind switch
        {
            SubtitleUpdateKind.Show => string.Equals(
                latest, update.SegmentId, StringComparison.Ordinal),
            SubtitleUpdateKind.Hide or SubtitleUpdateKind.Clear => latest is null,
            _ => false,
        };
    }

    private enum SubtitleUpdateKind
    {
        Show,
        Hide,
        Clear,
    }

    private readonly record struct SubtitleUpdate(
        SubtitleUpdateKind Kind,
        string SegmentId,
        string Text)
    {
        public static SubtitleUpdate Show(VoiceSubtitleCue cue) =>
            new(SubtitleUpdateKind.Show, cue.Id, cue.Text);

        public static SubtitleUpdate Hide(string segmentId) =>
            new(SubtitleUpdateKind.Hide, segmentId, "");

        public static SubtitleUpdate Clear() =>
            new(SubtitleUpdateKind.Clear, "", "");
    }
}
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the command from Step 2.

Expected: `Passed: 3, Failed: 0`.

- [ ] **Step 5: Commit only the pump files**

```powershell
git add -- gui-winui3/Services/VoiceSubtitleEventPump.cs gui-winui3.Tests/VoiceSubtitleEventPumpTests.cs
git commit -m "feat(subtitle): add non-blocking event pump"
```

### Task 2: Playback-start notifier and queue isolation

**Files:**
- Modify: `gui-winui3/Services/VoiceReplyAudioQueue.cs:9-167`
- Modify: `gui-winui3.Tests/VoicePlaybackQueueTests.cs:32-105`

- [ ] **Step 1: Rewrite the reply queue tests against the playback-start contract**

Keep the existing ordinary queue and sentence-buffer tests. Change reply-player lambdas to accept `(chunk, onPlaybackStarted, cancellationToken)` and call `onPlaybackStarted()` at the simulated audio start. Replace `ReplySubtitleChangesOnlyWhenItsAudioStartsPlaying` and add the blocked-renderer regression:

```csharp
[Fact]
public async Task ReplySubtitleChangesOnlyAfterPlaybackReportsStarted()
{
    using var presenter = new SubtitlePresenter();
    using var pump = new VoiceSubtitleEventPump(presenter);
    pump.SetEnabled(true);
    var allowPlaybackStart = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var shown = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    presenter.SnapshotChanged += snapshot =>
    {
        if (snapshot.Text == "Cau mot") shown.TrySetResult(true);
    };
    using var queue = new VoiceReplyAudioQueue(
        async (_, onPlaybackStarted, cancellationToken) =>
        {
            await allowPlaybackStart.Task.WaitAsync(cancellationToken);
            onPlaybackStarted();
        },
        subtitleEvents: pump);

    await queue.EnqueueAsync(
        Chunk("r1", 0),
        new VoiceSubtitleCue("r1", "Cau mot"));
    Assert.Equal(SubtitleSnapshot.Empty, presenter.Current);

    allowPlaybackStart.TrySetResult(true);
    await shown.Task.WaitAsync(TimeSpan.FromSeconds(1));
    await queue.CompleteAsync();
}

[Fact]
public async Task BlockedSubtitleRendererCannotBlockPlaybackContinuation()
{
    using var presenter = new SubtitlePresenter();
    using var pump = new VoiceSubtitleEventPump(presenter);
    pump.SetEnabled(true);
    var rendererEntered = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseRenderer = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var playbackContinued = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    presenter.SnapshotChanged += snapshot =>
    {
        if (snapshot != SubtitleSnapshot.Empty)
        {
            rendererEntered.TrySetResult(true);
            releaseRenderer.Task.GetAwaiter().GetResult();
        }
    };
    using var queue = new VoiceReplyAudioQueue(
        (_, onPlaybackStarted, _) =>
        {
            onPlaybackStarted();
            playbackContinued.TrySetResult(true);
            return Task.CompletedTask;
        },
        subtitleEvents: pump);

    await queue.EnqueueAsync(
        Chunk("r1", 0),
        new VoiceSubtitleCue("r1", "Cau mot"));
    await rendererEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

    await playbackContinued.Task.WaitAsync(TimeSpan.FromSeconds(1));
    Assert.True(playbackContinued.Task.IsCompletedSuccessfully);

    releaseRenderer.TrySetResult(true);
    await queue.CompleteAsync();
}
```

- [ ] **Step 2: Run the two queue tests and verify RED**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~ReplySubtitleChangesOnlyAfterPlaybackReportsStarted|FullyQualifiedName~BlockedSubtitleRendererCannotBlockPlaybackContinuation"
```

Expected: build fails because the current player delegate accepts two parameters and the queue has no `subtitleEvents` argument.

- [ ] **Step 3: Replace synchronous callbacks in `VoiceReplyAudioQueue`**

Use this public delegate and fields:

```csharp
public readonly record struct VoiceSubtitleCue(string Id, string Text);

public delegate Task VoiceReplyChunkPlayer(
    VoiceAudioChunk chunk,
    Action onPlaybackStarted,
    CancellationToken cancellationToken);

private readonly VoiceReplyChunkPlayer _playChunkAsync;
private readonly VoiceSubtitleEventPump? _subtitleEvents;
```

Replace the constructor with:

```csharp
public VoiceReplyAudioQueue(
    VoiceReplyChunkPlayer playChunkAsync,
    int capacity = 4,
    VoiceSubtitleEventPump? subtitleEvents = null)
{
    _playChunkAsync = playChunkAsync ?? throw new ArgumentNullException(nameof(playChunkAsync));
    _subtitleEvents = subtitleEvents;
    if (capacity < 1)
    {
        throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    _channel = Channel.CreateBounded<QueuedAudio>(
        new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    _consumer = ConsumeAsync(_cancellation.Token);
}
```

Replace `ConsumeAsync` and delete `InvokeSubtitleStarted`/`InvokeSubtitleEnded`:

```csharp
private async Task ConsumeAsync(CancellationToken cancellationToken)
{
    string? activeSubtitleId = null;
    try
    {
        await foreach (QueuedAudio queued in
            _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            VoiceSubtitleCue? cue = queued.SubtitleCue;
            int notified = 0;
            await _playChunkAsync(
                queued.Chunk,
                () =>
                {
                    if (Interlocked.Exchange(ref notified, 1) != 0 ||
                        cue is not VoiceSubtitleCue subtitle)
                    {
                        return;
                    }
                    if (_subtitleEvents?.TryShow(subtitle) == true)
                    {
                        activeSubtitleId = subtitle.Id;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    finally
    {
        if (activeSubtitleId is not null)
        {
            _subtitleEvents?.TryHide(activeSubtitleId);
        }
    }
}
```

Update `ReplyAudioQueueAcceptsNextChunkWhileCurrentChunkIsPlaying` so its lambda calls `onPlaybackStarted()` before waiting on the first-chunk gate.

- [ ] **Step 4: Run all `VoicePlaybackQueueTests` and verify GREEN**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~VoicePlaybackQueueTests"
```

Expected: every `VoicePlaybackQueueTests` test passes with zero failures.

- [ ] **Step 5: Commit the queue isolation**

```powershell
git add -- gui-winui3/Services/VoiceReplyAudioQueue.cs gui-winui3.Tests/VoicePlaybackQueueTests.cs
git commit -m "fix(subtitle): remove callbacks from voice playback path"
```

### Task 3: Voice service ownership, cancellation, and exact playback order

**Files:**
- Modify: `gui-winui3/Services/VoicePlaybackService.cs:20-49,220-266,384-445,549-653,774-857,927-944`
- Create: `gui-winui3.Tests/VoicePlaybackSubtitleIsolationTests.cs`

- [ ] **Step 1: Write failing service isolation tests**

Create `gui-winui3.Tests/VoicePlaybackSubtitleIsolationTests.cs`:

```csharp
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoicePlaybackSubtitleIsolationTests
{
    [Fact]
    public void ConfiguredSubtitlesStartDisabledAndTrackToggle()
    {
        using var api = new ApiService("test-secret", 65530);
        using var service = new VoicePlaybackService(api);
        using var presenter = new SubtitlePresenter();

        service.ConfigureSubtitles(presenter);
        Assert.False(service.SubtitlesEnabled);

        service.SubtitlesEnabled = true;
        Assert.True(service.SubtitlesEnabled);

        service.SubtitlesEnabled = false;
        Assert.False(service.SubtitlesEnabled);
    }

    [Fact]
    public async Task StopVoiceReturnsWhileSubtitleClearSubscriberIsBlocked()
    {
        using var api = new ApiService("test-secret", 65530);
        using var service = new VoicePlaybackService(api);
        using var presenter = new SubtitlePresenter();
        var clearEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClear = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConfigureSubtitles(presenter);
        service.SubtitlesEnabled = true;
        presenter.BeginSegment("r1", "Cau mot");
        presenter.SnapshotChanged += snapshot =>
        {
            if (snapshot == SubtitleSnapshot.Empty)
            {
                clearEntered.TrySetResult(true);
                releaseClear.Task.GetAwaiter().GetResult();
            }
        };

        Task stop = Task.Run(service.StopVoice);
        await clearEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await stop.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(stop.IsCompletedSuccessfully);

        releaseClear.TrySetResult(true);
    }
}
```

- [ ] **Step 2: Run the service tests and verify RED**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~VoicePlaybackSubtitleIsolationTests"
```

Expected: build fails because `ConfigureSubtitles` and `SubtitlesEnabled` do not exist.

- [ ] **Step 3: Add long-lived pump ownership to `VoicePlaybackService`**

Add a field and API:

```csharp
private VoiceSubtitleEventPump? _subtitleEvents;

public bool SubtitlesEnabled
{
    get => _subtitleEvents?.IsEnabled == true;
    set => _subtitleEvents?.SetEnabled(value);
}

public void ConfigureSubtitles(SubtitlePresenter presenter)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    _subtitleEvents?.Dispose();
    _subtitleEvents = new VoiceSubtitleEventPump(presenter);
}
```

Remove the `SubtitlePresenter` property. Construct the reply queue with the playback-start notifier and shared pump:

```csharp
var audioQueue = new VoiceReplyAudioQueue(
    (chunk, onPlaybackStarted, cancellationToken) =>
        PlayReplyChunkAsync(
            chunk,
            onPlaybackStarted,
            cancellationToken,
            latencyTracker),
    subtitleEvents: _subtitleEvents);
```

Replace both synchronous `SubtitlePresenter?.CancelAll()` calls in failure/stop paths with:

```csharp
_subtitleEvents?.TryClear();
```

Gate cue allocation in streaming and fallback paths:

```csharp
VoiceSubtitleCue? cue = !receivedAudio && SubtitlesEnabled
    ? new VoiceSubtitleCue(requestId, sentence)
    : null;
```

```csharp
VoiceSubtitleCue? cue = SubtitlesEnabled
    ? new VoiceSubtitleCue(requestId, sentence)
    : null;
await audioQueue.EnqueueAsync(
    new VoiceAudioChunk(requestId, 0, audio, 24_000, "wav"),
    cue,
    cancellationToken);
```

- [ ] **Step 4: Notify subtitle only after `MediaPlayer.Play()`**

Change the reply player and WAV method signatures:

```csharp
private Task PlayReplyChunkAsync(
    VoiceAudioChunk chunk,
    Action onPlaybackStarted,
    CancellationToken cancellationToken,
    VoiceReplyLatencyTracker latencyTracker)
{
    latencyTracker.MarkPlaybackRequested();
    return PlayWavAsync(
        chunk.Audio,
        cancellationToken,
        onPlaybackStarted: onPlaybackStarted);
}
```

```csharp
private async Task PlayWavAsync(
    byte[] audio,
    CancellationToken cancellationToken,
    string? requestId = null,
    Action? onPlaybackStarted = null)
```

Immediately after the existing `player.Play()` line, add only the safe internal notifier:

```csharp
player.Play();
try
{
    onPlaybackStarted?.Invoke();
}
catch (Exception ex)
{
    Debug.WriteLine($"[VoicePlayback] Playback-start observer failed: {ex.Message}");
}
await ended.Task.WaitAsync(cancellationToken);
```

Dispose the event pump after `StopVoice()` in both disposal paths:

```csharp
_subtitleEvents?.Dispose();
_subtitleEvents = null;
```

- [ ] **Step 5: Run the service and queue tests and verify GREEN**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~VoicePlaybackSubtitleIsolationTests|FullyQualifiedName~VoicePlaybackQueueTests|FullyQualifiedName~VoiceSubtitleEventPumpTests"
```

Expected: all focused subtitle/voice tests pass with zero failures.

- [ ] **Step 6: Commit the service integration**

```powershell
git add -- gui-winui3/Services/VoicePlaybackService.cs gui-winui3.Tests/VoicePlaybackSubtitleIsolationTests.cs
git commit -m "fix(voice): keep subtitles off the playback critical path"
```

### Task 4: Persisted toggle wiring with zero work when disabled

**Files:**
- Modify: `gui-winui3/MainWindow.xaml.cs:108-113`
- Modify: `gui-winui3/Views/VoiceView.xaml.cs:93-104`

- [ ] **Step 1: Configure the presenter through the voice service**

In `MainWindow.xaml.cs`, replace the direct property assignment:

```csharp
_voicePlayback.ConfigureSubtitles(subtitlePresenter);
```

- [ ] **Step 2: Apply the toggle to voice before overlay lifecycle**

Replace `ApplySubtitleToggle` with:

```csharp
private void ApplySubtitleToggle(bool isOn)
{
    if (_voicePlayback is not null)
    {
        _voicePlayback.SubtitlesEnabled = isOn;
    }
    if (_overlayService == null)
    {
        return;
    }
    if (isOn)
    {
        _overlayService.Start();
    }
    else
    {
        _overlayService.Pause();
    }
}
```

- [ ] **Step 3: Build the WinUI project**

```powershell
dotnet build gui-winui3/RuApp.csproj --configuration Release --no-restore
```

Expected: exit code 0 with zero compile errors.

- [ ] **Step 4: Commit toggle wiring only**

```powershell
git add -- gui-winui3/MainWindow.xaml.cs gui-winui3/Views/VoiceView.xaml.cs
git commit -m "fix(subtitle): disable cue work with the toggle"
```

### Task 5: Regression, documentation, and release build

**Files:**
- Modify: `README.md:58-87`
- Modify: `docs/superpowers/specs/2026-08-09-ru-vts-subtitle-design.md`
- Delete: `docs/superpowers/plans/2026-08-09-ru-vts-subtitle.md`

- [ ] **Step 1: Run the complete WinUI test suite**

```powershell
dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj --configuration Release --no-restore
```

Expected after adding seven tests: `Failed: 0, Passed: 77, Total: 77`.

- [ ] **Step 2: Re-run backend settings integration tests**

```powershell
.\.venv\Scripts\python.exe -m pytest tests/test_server_api.py -q
```

Expected: `127 passed`; deprecation warnings are environment warnings, not subtitle failures.

- [ ] **Step 3: Update current documentation**

Add this contract to the README subtitle paragraph:

```markdown
Subtitle delivery is fail-open: `MediaPlayer.Play()` runs before a bounded,
non-blocking latest-state notification. A stalled or failed subtitle renderer
may drop text but cannot delay or fail voice playback; disabling subtitles also
disables cue creation and VTS window polling.
```

In the design spec, change status to implemented but awaiting live A/B verification. Record the focused/full test counts and keep the live acceptance gate explicit. Keep the old karaoke implementation plan deleted because it contradicts the approved plain-text, fail-open design.

- [ ] **Step 4: Scan for stale implementation and debug artifacts**

```powershell
rg -n "onSubtitleStarted|onSubtitleEnded|SubtitlePresenter\?\.CancelAll|\[DEBUG-" gui-winui3 gui-winui3.Tests
rg -n -i "karaoke|SubtitleTiming|callback subtitle.*trước playback" README.md docs gui-winui3 gui-winui3.Tests
git diff --check
```

Expected: no production synchronous subtitle callbacks, no debug tags, no `SubtitleTiming` references, no outdated karaoke claims, and no whitespace errors. The design-spec evidence paragraph may retain the phrase describing the removed callback as historical evidence.

- [ ] **Step 5: Publish a fresh Release build**

```powershell
cmd.exe /d /c build.bat
```

Expected: exit code 0, `RuApp.pri created successfully`, and output at `gui-winui3/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/RuApp.exe`.

- [ ] **Step 6: Commit only scoped documentation and the obsolete-plan deletion**

```powershell
git add -- README.md docs/superpowers/specs/2026-08-09-ru-vts-subtitle-design.md docs/superpowers/plans/2026-08-09-ru-vts-subtitle.md
git commit -m "docs: document fail-open subtitle delivery"
```

- [ ] **Step 7: Live acceptance handoff**

Do not auto-launch or control Ru/VTube Studio. Ask the user to warm OmniVoice, run the same reply at least five times with subtitles off and five times on, and confirm audible start latency. Compare numeric `first-audio → playback-request` samples; do not call the result end-to-end accepted until the user confirms audio and the subtitle remains visible across three consecutive sentences, VTS move/minimize/restore, and app restart.
