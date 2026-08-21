# Hands-free Voice Conversation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Restore a user-controlled, half-duplex voice conversation loop in the WinUI 3 Chat: listen, detect the end of one spoken turn, transcribe, send automatically, wait for Ru's existing streamed reply to finish, and listen again until the user stops the mode.

**Architecture:** Keep ASP.NET Core, the Runtime Worker, STT, and OmniVoice contracts unchanged. Add a pure PCM voice-activity detector and WAV encoder, wrap native Windows AudioGraph capture behind a testable interface, and put a cancel-safe VoiceConversationService between those services and ChatView. ChatView remains the owner of Chat history, WebSocket ordering, visible status, and the reply-completion boundary. The primary mic action becomes the explicit conversation-mode entry point; typed Chat remains available when the mode is idle.

**Tech Stack:** C#/.NET 10, WinUI 3, Windows AudioGraph/AudioFrameOutputNode, existing ApiService, ChatWebSocketClient, VoicePlaybackService, xUnit, and sequential dotnet test/build.bat verification.

---

## Coordination boundary with the parallel UI agent

The other agent owns the current UI redesign. Do not edit or stage gui-winui3/Views/ChatView.xaml or gui-winui3/Views/ChatView.xaml.cs while that agent has uncommitted changes or an active UI commit is being prepared. Before every integration step, run:

~~~powershell
git status --short --branch
git diff --name-only
git diff --cached --name-only
git log --oneline --decorate -6
git worktree list --porcelain
~~~

If either ChatView file is dirty, finish only the non-UI tasks and stop at the integration checkpoint. Do not use git reset --hard, git checkout --, force push, or delete the other agent's worktree. Do not reuse D:\LLM\.worktrees\codex-task-4-omnivoice-native-speed-recovery; it is already dirty and unrelated.

At execution time, invoke superpowers:using-git-worktrees and create a fresh feature worktree from the then-current main commit:

~~~powershell
git worktree add -b codex/handsfree-voice D:\LLM\.worktrees\codex-handsfree-voice main
~~~

All implementation commits belong on codex/handsfree-voice. Integration into the agent's UI work happens only after the UI worktree/commit is identified and the shared file diff is reviewed.

## File map

| File | Responsibility | Planned action |
| --- | --- | --- |
| gui-winui3/Services/VoiceActivityDetector.cs | Pure RMS/VAD state and turn boundaries | Create |
| gui-winui3/Services/PcmWavEncoder.cs | Bounded in-memory PCM WAV encoding | Create |
| gui-winui3/Services/VoiceTurnCaptureService.cs | Native Windows audio frames and capture lifetime | Create |
| gui-winui3/Services/VoiceConversationService.cs | Start/stop loop, state transitions, exactly-once turn handoff | Create |
| gui-winui3/Services/ApiService.cs | Cancellable STT request overload | Modify |
| gui-winui3/Views/ChatView.xaml.cs | Chat transport adapter, reply completion, lifecycle wiring | Modify only at UI checkpoint |
| gui-winui3/Views/ChatView.xaml | Mic/status presentation and accessibility names | Modify only at UI checkpoint |
| gui-winui3/Services/MicRecorderService.cs | Old one-shot recorder | Delete after reference audit |
| gui-winui3/RuApp.csproj | Enable native audio-buffer interop | Modify |
| gui-winui3.Tests/VoiceActivityDetectorTests.cs | Deterministic VAD tests | Create |
| gui-winui3.Tests/PcmWavEncoderTests.cs | WAV header/size/privacy-boundary tests | Create |
| gui-winui3.Tests/VoiceTurnCaptureServiceTests.cs | Capture lifecycle tests with a fake frame source | Create |
| gui-winui3.Tests/VoiceConversationServiceTests.cs | State machine and cancellation tests | Create |
| gui-winui3.Tests/ChatViewVoiceContractTests.cs | Static UI/privacy contract tests | Create or modify only at UI checkpoint |
| docs/operations/runtime-behavior.md | Current voice behavior | Modify |
| docs/operations/verification.md | Automated/manual acceptance gates | Modify |
| README.md | User-facing capability summary | Modify |

The backend route contract, Python STT worker, VoiceStreamClient, VoicePlaybackQueue, VoiceReplyAudioQueue, subtitle pump, and VTS protocol are not implementation targets for this feature.

## Task 1: Establish the isolated branch and baseline

**Files:** No source changes.

- [ ] **Step 1: Create the isolated worktree using the required worktree skill.**

Invoke superpowers:using-git-worktrees, create the exact worktree named in the coordination boundary, and confirm the new branch is based on the latest current main without copying uncommitted UI edits.

- [ ] **Step 2: Record the baseline status and focused test result.**

Run from D:\LLM\.worktrees\codex-handsfree-voice:

~~~powershell
git status --short --branch
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
~~~

Expected: the worktree has no unexpected changes and the existing WinUI test suite passes before feature code is added. If restore is required, run dotnet restore once and repeat the test.

- [ ] **Step 3: Commit no code and verify the shared UI boundary.**

~~~powershell
git diff --name-only
git status --short
~~~

Expected: neither ChatView.xaml nor ChatView.xaml.cs is modified by this branch before the UI checkpoint.

## Task 2: Add the pure voice-activity detector using TDD

**Files:**
- Create: gui-winui3/Services/VoiceActivityDetector.cs
- Test: gui-winui3.Tests/VoiceActivityDetectorTests.cs

- [ ] **Step 1: Write failing tests for the public detector contract.**

Define this exact public shape in the test first:

~~~csharp
var options = VoiceActivityOptions.Default;
var detector = new VoiceActivityDetector(options);

VoiceActivityFrameResult result = detector.ProcessFrame(Samples(320, 0.20));

Assert.True(result.IsSpeech);
Assert.False(result.ShouldComplete);
    Assert.Equal(VoiceActivityState.WaitingForSpeech, result.State);
~~~

Add tests with deterministic 320-sample frames for all of these cases:

1. The first five consecutive 20 ms voiced frames remain in WaitingForSpeech and the sixth frame transitions to Speaking after the 120 ms speech-start requirement.
2. A silent frame below the adaptive threshold is not classified as speech.
3. Forty-three consecutive silent frames after speech complete the turn after the 860 ms trailing-silence boundary, with no empty completion before speech has started.
4. Three thousand frames complete a turn at the 60-second maximum even when speech continues.
5. Reset clears speech/silence counters and makes the next frame belong to a new turn.
6. RMS calculation uses normalized 16-bit PCM and does not classify clipped negative samples differently from positive samples of the same magnitude.

Use this helper in the test file so no audio device is needed:

~~~csharp
private static short[] Samples(int count, double amplitude)
{
    short value = (short)Math.Round(Math.Clamp(amplitude, -1.0, 1.0) * short.MaxValue);
    return Enumerable.Repeat(value, count).ToArray();
}
~~~

- [ ] **Step 2: Run the detector tests and verify the expected compile failure.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --filter FullyQualifiedName~VoiceActivityDetectorTests --nologo
~~~

Expected: FAIL because VoiceActivityOptions, VoiceActivityDetector, and VoiceActivityFrameResult do not exist yet.

- [ ] **Step 3: Implement the minimal detector contract.**

Implement these exact types in VoiceActivityDetector.cs:

~~~csharp
public enum VoiceActivityState
{
    WaitingForSpeech,
    Speaking,
    Completed,
}

public sealed record VoiceActivityOptions(
    int SampleRate,
    int FrameSamples,
    TimeSpan SpeechStart,
    TimeSpan TrailingSilence,
    TimeSpan MaxTurn,
    double MinimumRms,
    double NoiseMultiplier)
{
    public static VoiceActivityOptions Default { get; } = new(
        16_000,
        320,
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(850),
        TimeSpan.FromSeconds(60),
        0.018,
        3.0);
}

public readonly record struct VoiceActivityFrameResult(
    VoiceActivityState State,
    bool IsSpeech,
    bool ShouldComplete,
    bool HasSpeech);
~~~

ProcessFrame(ReadOnlySpan<short>) must calculate normalized RMS, keep an EMA noise floor only while waiting for speech, and classify a frame as speech when rms >= max(MinimumRms, noiseFloor * NoiseMultiplier). Require six voiced frames before setting HasSpeech; after that, count silent frames and complete at TrailingSilence. Enforce MaxTurn by frame count. Reject frames with a sample count other than FrameSamples with ArgumentException. Reset must restore all counters and the initial noise floor.

- [ ] **Step 4: Run the detector tests and commit the pure component.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --filter FullyQualifiedName~VoiceActivityDetectorTests --nologo
git diff --check
git add gui-winui3/Services/VoiceActivityDetector.cs gui-winui3.Tests/VoiceActivityDetectorTests.cs
git commit -m "feat: add bounded voice activity detection"
~~~

Expected: all detector tests PASS.

## Task 3: Add a pure PCM WAV encoder using TDD

**Files:**
- Create: gui-winui3/Services/PcmWavEncoder.cs
- Test: gui-winui3.Tests/PcmWavEncoderTests.cs

- [ ] **Step 1: Write failing encoder tests.**

Test the exact public method PcmWavEncoder.Encode(IReadOnlyList<short> samples, int sampleRate, short channels) with a four-sample fixture. Assert:

- output is exactly 44 bytes plus samples.Count * 2;
- bytes 0-3 are RIFF, bytes 8-11 are WAVE, bytes 12-15 are fmt , and bytes 36-39 are data;
- format is PCM (1), mono (1), 16 kHz, 16-bit;
- RIFF and data lengths match the actual payload;
- little-endian sample bytes preserve both a positive and a negative sample;
- an empty input throws ArgumentException and does not produce a request.

- [ ] **Step 2: Run the focused test and verify it fails.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --filter FullyQualifiedName~PcmWavEncoderTests --nologo
~~~

Expected: FAIL because PcmWavEncoder does not exist.

- [ ] **Step 3: Implement the bounded encoder.**

Write the 44-byte RIFF header with BinaryPrimitives.WriteUInt32LittleEndian and WriteUInt16LittleEndian, validate sampleRate is between 8,000 and 192,000, validate channels == 1, and copy each short as little-endian PCM. Do not write a file or log the samples. Keep the return value as byte[] so the only lifetime of the audio is the in-memory STT request.

- [ ] **Step 4: Run tests, check whitespace, and commit.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --filter FullyQualifiedName~PcmWavEncoderTests --nologo
git diff --check
git add gui-winui3/Services/PcmWavEncoder.cs gui-winui3.Tests/PcmWavEncoderTests.cs
git commit -m "feat: encode voice turns as bounded PCM WAV"
~~~

Expected: all encoder tests PASS and the commit contains no audio fixture.

## Task 4: Replace the old recorder with a testable native turn capture

**Files:**
- Create: gui-winui3/Services/VoiceTurnCaptureService.cs
- Modify: gui-winui3/RuApp.csproj
- Test: gui-winui3.Tests/VoiceTurnCaptureServiceTests.cs

- [ ] **Step 1: Write fake-driven capture lifecycle tests.**

Define this exact interface and result contract in the test seam:

~~~csharp
public enum VoiceTurnStopReason
{
    SpeechEnded,
    MaximumDuration,
    NoSpeech,
    Cancelled,
    CaptureError,
}

public sealed record VoiceTurnCaptureResult(
    byte[]? WavBytes,
    VoiceTurnStopReason Reason);

public interface IVoiceTurnCapture : IDisposable
{
    bool IsCapturing { get; }
    Task<VoiceTurnCaptureResult> CaptureTurnAsync(CancellationToken cancellationToken);
    Task StopAsync();
}
~~~

Use a fake frame source in tests. Cover one successful voiced turn, no-speech completion with WavBytes == null, maximum-duration completion, cancellation, second CaptureTurnAsync after cleanup, and two concurrent capture calls where the second call fails with InvalidOperationException. Assert that every path calls Stop/disposes the graph exactly once.

- [ ] **Step 2: Run the capture tests and verify they fail.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --filter FullyQualifiedName~VoiceTurnCaptureServiceTests --nologo
~~~

Expected: FAIL because the capture interface and implementation do not exist.

- [ ] **Step 3: Implement native AudioGraph capture behind the interface.**

Use AudioGraphSettings with AudioEncodingProperties.CreatePcm(16_000, 1, 16), DesiredSamplesPerQuantum = 320, and AudioGraph.CreateAsync. Create a speech device input node and an AudioFrameOutputNode with the same PCM format, connect the input to the output, and call GetFrame synchronously from the graph's QuantumStarted handler. Microsoft documents this synchronized frame-output pattern and the IMemoryBufferByteAccess bridge for reading raw audio buffers.

Declare the native buffer bridge once in this service using the documented interface GUID 5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D; enable unsafe blocks in RuApp.csproj with:

~~~xml
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
~~~

Use this exact interop declaration; the pointer is used only inside the locked
buffer scope:

~~~csharp
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
private unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}
~~~

In the quantum handler:

1. Lock the frame buffer for read and copy its bytes immediately; never retain a pointer after the buffer/reference is disposed.
2. Convert float frames to normalized short samples if Windows reports IEEE float; copy PCM16 frames directly when the frame subtype is PCM.
3. Feed exact 320-sample frames to VoiceActivityDetector.
4. Keep a deque containing the last 250 ms of samples while waiting for speech.
5. Once speech starts, append the pre-roll and all subsequent frames to a per-turn List<short>.
6. When the detector says complete, signal the awaiting task; stop the graph, dispose the input/output nodes and graph, and encode the samples with PcmWavEncoder only if HasSpeech is true.

The quantum handler must not await, touch XAML, or call ApiService. Capture errors complete the current task with CaptureError; cancellation completes with Cancelled. StopAsync is idempotent, cancels the frame signal, stops the graph, disposes all native objects, clears sample buffers, and releases the microphone before returning.

- [ ] **Step 4: Run capture tests and compile the WinUI project.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --filter FullyQualifiedName~VoiceTurnCaptureServiceTests --nologo
dotnet build D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3\RuApp.csproj --no-restore --nologo
~~~

Expected: fake lifecycle tests PASS and the WinUI project compiles with zero errors. If the target Windows SDK exposes a float frame despite the requested PCM format, the conversion branch must be covered by a pure helper test before continuing.

- [ ] **Step 5: Remove the obsolete recorder only after the reference audit.**

~~~powershell
rg -n "MicRecorderService|new MicRecorderService|StartAsync\(|StopAsync\(" gui-winui3 gui-winui3.Tests
~~~

After ChatView has been migrated in Task 7 and MicRecorderService has no remaining references, delete gui-winui3/Services/MicRecorderService.cs. If a remaining non-voice feature still references it, keep the file and document the reference in the integration commit instead of deleting it.

- [ ] **Step 6: Commit the capture component.**

~~~powershell
git diff --check
git add gui-winui3/RuApp.csproj gui-winui3/Services/VoiceTurnCaptureService.cs gui-winui3.Tests/VoiceTurnCaptureServiceTests.cs
git commit -m "feat: capture bounded voice turns with native audio graph"
~~~

Add the recorder deletion to this commit only if the reference audit in Step 5 is clean.

## Task 5: Add the cancel-safe conversation state machine using TDD

**Files:**
- Create: gui-winui3/Services/VoiceConversationService.cs
- Test: gui-winui3.Tests/VoiceConversationServiceTests.cs

- [ ] **Step 1: Write fake-port tests for the conversation loop.**

Define this exact port and state surface:

~~~csharp
public enum VoiceConversationState
{
    Idle,
    Listening,
    Transcribing,
    Sending,
    Speaking,
    Error,
}

public interface IVoiceConversationPort
{
    Task<VoiceTurnCaptureResult> CaptureTurnAsync(CancellationToken cancellationToken);
    Task StopCaptureAsync();
    Task<string?> TranscribeAudioAsync(byte[] wavBytes, CancellationToken cancellationToken);
    Task SendPromptAndWaitForReplyAsync(string text, CancellationToken cancellationToken);
}

public sealed class VoiceConversationService : IDisposable
{
    public VoiceConversationState State { get; }
    public bool IsActive { get; }
    public event Action<VoiceConversationState, string?>? StateChanged;
    public Task StartAsync(CancellationToken cancellationToken = default);
    public Task StopAsync();
    public void Dispose();
}
~~~

Use a fake port and assert:

1. StartAsync enters Listening and requests one capture.
2. A valid WAV transitions through Transcribing, Sending, and Speaking, calls SendPromptAndWaitForReplyAsync exactly once, then returns to Listening after that task completes.
3. NoSpeech does not call STT or send and returns to Listening with the transient message Không nghe rõ, thử lại nha.
4. Empty STT text does not send and returns to Listening.
5. STT exception or reply exception enters Error and exits the loop.
6. StopAsync is idempotent, cancels a blocked capture/transcription/reply, and no delayed result can trigger a send afterward.
7. Two StartAsync calls share one active loop and never create duplicate captures.
8. A second turn is not requested until the first reply task completes.

- [ ] **Step 2: Run the state-machine tests and verify they fail.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --filter FullyQualifiedName~VoiceConversationServiceTests --nologo
~~~

Expected: FAIL because the state machine types do not exist.

- [ ] **Step 3: Implement the serialized run loop.**

Use one private _runTask, one linked _sessionCts, and a lock protecting start/stop replacement. StartAsync returns the existing _runTask when already active. StopAsync atomically detaches/cancels the CTS, calls IVoiceConversationPort.StopCaptureAsync, awaits the run task, and emits Idle exactly once.

The loop must follow this exact order:

~~~text
Idle -> Listening -> Transcribing -> Sending -> Speaking -> Listening
                         \-> Error -> Idle
~~~

After every await, check the linked token before sending or starting the next capture. Treat NoSpeech and an empty transcription as recoverable listening conditions. Treat capture errors, STT failures, chat errors, and voice-reply failures as terminal errors. Do not log text, audio, transcripts, or the status message; diagnostics may record only bounded state/outcome names.

- [ ] **Step 4: Run all focused service tests and commit.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --filter FullyQualifiedName~VoiceConversationServiceTests --nologo
git diff --check
git add gui-winui3/Services/VoiceConversationService.cs gui-winui3.Tests/VoiceConversationServiceTests.cs
git commit -m "feat: add cancel-safe voice conversation loop"
~~~

Expected: all state-machine tests PASS.

## Task 6: Add cancellable STT transport without changing the route

**Files:**
- Modify: gui-winui3/Services/ApiService.cs
- Test: extend gui-winui3.Tests/VoiceConversationServiceTests.cs only if a transport seam needs coverage

- [ ] **Step 1: Add the cancellation-aware overload without breaking callers.**

Keep the current method as a compatibility wrapper:

~~~csharp
public Task<string?> TranscribeAudioAsync(byte[] wavBytes) =>
    TranscribeAudioAsync(wavBytes, CancellationToken.None);

public async Task<string?> TranscribeAudioAsync(
    byte[] wavBytes,
    CancellationToken cancellationToken)
{
    try
    {
        string audioB64 = Convert.ToBase64String(wavBytes);
        var payload = new { audio_b64 = audioB64, sample_rate = 16_000 };
        using var response = await _httpClient.PostAsJsonAsync(
            $"{_baseUrl}/voice/transcribe", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: cancellationToken);
        return result.TryGetProperty("text", out var text)
            ? text.GetString()
            : null;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ApiService] TranscribeAudioAsync failed: {ex.Message}");
        return null;
    }
}
~~~

Preserve the existing audio_b64 and sample_rate = 16000 payload, catch only non-cancellation failures for the existing null/error behavior, and rethrow OperationCanceledException when the supplied token is cancelled. Do not add a new endpoint or include transcript/audio content in Debug.WriteLine.

- [ ] **Step 2: Run the existing GUI suite and commit the transport seam.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
git diff --check
git add gui-winui3/Services/ApiService.cs
git commit -m "feat: cancel speech transcription requests"
~~~

Expected: existing tests remain green and no backend route diff appears.

## Task 7: Integrate the controller with ChatView after the UI-agent checkpoint

**Files:**
- Modify: gui-winui3/Views/ChatView.xaml.cs
- Modify: gui-winui3/Views/ChatView.xaml
- Create or modify: gui-winui3.Tests/ChatViewVoiceContractTests.cs

- [ ] **Step 1: Recheck the UI agent's status before opening either ChatView file.**

Run the coordination commands at the top of this plan. If either ChatView file is dirty, do not edit it. If the UI agent has committed changes, inspect the commit and rebase this feature branch from the current shared base without discarding either side. Record the exact UI commit hash in the integration commit message.

- [ ] **Step 2: Add a reply-completion adapter in ChatView.**

Keep the existing visible message update and VoicePlaybackService pipeline. Add a private TaskCompletionSource for the active voice prompt, created before the prompt is sent and completed only at the end of OnWsDoneReceived:

~~~csharp
private TaskCompletionSource<bool>? _voiceReplyCompletion;
private long _voiceReplyGeneration;

private static TaskCompletionSource<bool> NewVoiceCompletion() =>
    new(TaskCreationOptions.RunContinuationsAsynchronously);
~~~

The transport adapter must use this ordering for one voice turn:

~~~csharp
private async Task SendPromptAndWaitForReplyAsync(
    string text,
    CancellationToken cancellationToken)
{
    TaskCompletionSource<bool> completion = NewVoiceCompletion();
    long generation = Interlocked.Increment(ref _voiceReplyGeneration);
    _voiceReplyCompletion = completion;

    await EnqueueOnUiAsync(() => SendMessageTextAsync(text));
    using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
    {
        await completion.Task;
    }

    if (generation != Volatile.Read(ref _voiceReplyGeneration))
    {
        throw new OperationCanceledException(cancellationToken);
    }
}
~~~

EnqueueOnUiAsync must return a Task and propagate exceptions from the UI
delegate; do not use fire-and-forget for this send. On every terminal path, set
_voiceReplyCompletion to null only if it still references the same TCS.

In OnWsDoneReceived, keep the current display-marker stripping and message
update, await CompleteReplyStreamAsync(finalVoiceText) when auto-read is
enabled, then set the TCS result. When auto-read is disabled, set the result
immediately after the Chat done update. OnWsErrorReceived, WebSocket disconnect,
reset, session change, and Dispose must fault/cancel the matching TCS and
increment the generation so a stale callback cannot wake a new voice turn.

- [ ] **Step 3: Replace the primary mic handler with the conversation entry point.**

Construct one VoiceTurnCaptureService and one VoiceConversationService in ChatView.Initialize. Implement IVoiceConversationPort in ChatView by wiring:

~~~text
CaptureTurnAsync       -> VoiceTurnCaptureService.CaptureTurnAsync
StopCaptureAsync       -> VoiceTurnCaptureService.StopAsync
TranscribeAudioAsync   -> ApiService.TranscribeAudioAsync(wav, token)
SendPromptAndWait...   -> ChatView's existing WebSocket send + reply TCS
~~~

OnMicClick must only start or stop the controller. It must not perform STT or write the transcript to ChatInputTextBox. Manual typed Send, suggestion click, session activation, and disposal must stop the controller before using the existing typed-chat path. Do not send the voice status strings as prompts.

- [ ] **Step 4: Add the minimal XAML status surface without rewriting the UI layout.**

At the existing composer, retain the current input/send controls and add only:

~~~xml
<TextBlock x:Name="VoiceConversationStatusText"
           Text=""
           Visibility="Collapsed"
           AutomationProperties.LiveSetting="Polite"
           Foreground="{StaticResource RuMutedBrush}" />
~~~

Update the mic button tooltip and AutomationProperties.Name from the old one-shot STT wording to the start/stop conversation wording. Reuse existing brushes and glyphs; add no Unicode emoji, decorative voice metadata, or new backend status indicator. Preserve the other agent's responsive/layout changes exactly.

- [ ] **Step 5: Add contract tests for wiring and privacy.**

Assert that ChatView.xaml contains the named status element, polite live setting, start/stop wording, and no prohibited emoji. Assert that ChatView.xaml.cs contains the controller start/stop wiring and does not assign voice transcripts directly to ChatInputTextBox.Text. Assert that ChatView still calls CompleteReplyStreamAsync before completing the voice reply seam.

- [ ] **Step 6: Run focused integration tests and commit only the shared UI seam.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --filter FullyQualifiedName~ChatViewVoiceContractTests --nologo
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
git diff --check
git add gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/ChatViewVoiceContractTests.cs
git commit -m "feat: restore hands-free voice chat in WinUI"
~~~

Expected: focused and full GUI tests PASS. If the UI agent's changes overlap, stop and resolve the exact hunk with the current source; never overwrite the other agent's committed design wholesale.

## Task 8: Verify dead-code cleanup and update current documents

**Files:**
- Modify: docs/operations/runtime-behavior.md
- Modify: docs/operations/verification.md
- Modify: README.md
- Delete: gui-winui3/Services/MicRecorderService.cs only after the reference audit in Task 4

- [ ] **Step 1: Verify no stale recorder or old one-shot references remain.**

~~~powershell
rg -n "MicRecorderService|Nói cho Ru nghe \\(STT\\)|TranscribeAudioAsync\\(wav\\)" gui-winui3 gui-winui3.Tests docs README.md
~~~

Expected: no stale MicRecorderService reference if it was deleted, no old one-shot tooltip, and only the new cancellable STT call remains.

- [ ] **Step 2: Update the runtime behavior document.**

Under Speech input and VTube Studio, document that voice conversation mode is explicitly started from Chat, uses bounded 16 kHz mono PCM WAV turns, detects a bounded trailing silence, sends only non-empty STT results, pauses capture while Ru speaks, and stops on user cancellation/errors. Preserve the existing statement that audio leaves the machine only for the explicitly requested STT operation and is not persisted.

- [ ] **Step 3: Update the verification document.**

Add the exact focused commands for the VAD, WAV, capture, controller, and ChatView contract tests. Add the separate manual gate for two spoken turns, natural pauses, mixed Vietnamese-English, stop during each phase, session switching, speaker output, subtitles, and VTube Studio. State explicitly that passing automated tests does not prove a physical microphone or speaker.

- [ ] **Step 4: Update README capability text.**

Add one bullet under current behavior: Chat has an explicit hands-free half-duplex voice mode that automatically transcribes and sends turns and waits for the existing OmniVoice reply before listening again. Keep the existing Google vi-VN experimental STT and live hardware acceptance wording.

- [ ] **Step 5: Check all documentation and commit.**

~~~powershell
rg -n "voice|STT|microphone|hands-free|half-duplex|one-shot|MicRecorderService" README.md docs gui-winui3
git diff --check
git add README.md docs/operations/runtime-behavior.md docs/operations/verification.md
git add -u gui-winui3/Services/MicRecorderService.cs
git commit -m "docs: document hands-free voice conversation"
~~~

Expected: current documents describe the new behavior, no outdated one-shot description remains, and no training/model documentation is reintroduced.

## Task 9: Run sequential verification before handoff

- [ ] **Step 1: Run the focused deterministic GUI tests.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --filter "FullyQualifiedName~VoiceActivityDetectorTests|FullyQualifiedName~PcmWavEncoderTests|FullyQualifiedName~VoiceTurnCaptureServiceTests|FullyQualifiedName~VoiceConversationServiceTests|FullyQualifiedName~ChatViewVoiceContractTests" --nologo
~~~

Expected: all new tests PASS.

- [ ] **Step 2: Run the complete WinUI and backend suites sequentially.**

~~~powershell
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
dotnet test D:\LLM\.worktrees\codex-handsfree-voice\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo
~~~

Expected: both suites PASS. Do not run heavyweight Python, backend, and WinUI gates in parallel because this workspace has previously hit Windows pagefile pressure under parallel load.

- [ ] **Step 3: Run route, privacy, and whitespace gates.**

From the canonical checkout after the feature branch is integrated, run:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
git diff --check
~~~

Expected: route count remains 41 HTTP routes, 2 WebSocket routes, including the unchanged /voice/transcribe and /voice/stream contracts, and diff check has no output.

- [ ] **Step 4: Build the desktop stack sequentially.**

Run from D:\LLM after integrating the feature branch, not from a worktree if build.bat resolves project paths relative to the canonical checkout:

~~~powershell
cmd /c D:\LLM\build.bat
~~~

Expected: self-contained RuBackend.exe and RuApp.exe build with zero errors.

- [ ] **Step 5: Perform the user-run live acceptance.**

With the real backend, OmniVoice, microphone, speakers, and optional VTube Studio running, verify the six manual scenarios in the spec. Record only bounded outcomes such as turn count, audio played, stop outcome, and whether markers stayed private; never place raw audio or transcripts in artifacts. Automated test/build success must not be reported as live microphone or speaker acceptance.

- [ ] **Step 6: Final ownership review.**

~~~powershell
git status --short --branch
git diff --name-only main...HEAD
git log --oneline --decorate -12
~~~

Expected: the feature branch contains only voice-service, explicitly reviewed ChatView integration, tests, and current docs; unrelated UI-agent work and the existing cleanup changes are preserved and not included accidentally.
