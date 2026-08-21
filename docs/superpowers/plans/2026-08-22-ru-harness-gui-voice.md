# Ru Harness vNext GUI Voice Implementation Plan

> Implementation status: complete on `main` as of 2026-08-22. The backend
> single-receive WebSocket fix was added alongside the GUI Harness because
> sequential voice relistening exposed a real lost-prompt race.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Run the real WinUI Ru shell through a typed external-session composition seam and verify one-to-one voice turns with deterministic frame input, deterministic backend audio, UI Automation, cancellation, session replacement, and privacy checks without opening physical devices.

**Architecture:** Keep production startup unchanged behind an IRuBackendSessionProvider, then add an explicit RuAppComposition selected by a short-lived restricted session descriptor. The descriptor points the app at an already-owned deterministic backend and selects a deterministic frame source plus a recording playback device; the same ChatView, VoiceConversationService, API clients, WebSocket clients, and reply lifecycle execute in both modes.

**Tech Stack:** C#/.NET 10, WinUI 3, Windows UI Automation through the Windows desktop framework, xUnit, ClientWebSocket, deterministic PCM frames, JSONL harness events, PowerShell orchestration, and the backend Harness v2 artifact.

---

## 0. Scope and working-tree boundary

Work stays on main and must preserve the current dirty WinUI, voice-fix,
documentation, and cleanup-policy changes. Stage only the files named by each
task. The GUI plan depends on the backend Harness v2 session API; it does not
change the public 41 HTTP/2 WebSocket route contract.

The GUI Harness is an explicit interactive Windows job. It must fail with a
clear existing_instance result when another RuApp instance is already open; it
must not close or take over a user's running application. It never requests
microphone permission, creates an AudioGraph, opens a speaker MediaPlayer,
connects to VTube Studio, or stores audio/transcript content in its result.

## 1. File map

Create the composition and endpoint abstractions:

- gui-winui3/Services/BackendSession.cs: validated HTTP/WebSocket endpoint and
  bearer value used by all public clients;
- gui-winui3/Services/IRuBackendSessionProvider.cs: lifecycle contract consumed
  by MainWindow;
- gui-winui3/Services/ManagedRuBackendSessionProvider.cs: production provider
  wrapping BackendProcessManager;
- gui-winui3/Services/ExternalRuBackendSessionProvider.cs: no-child provider
  for a Harness-owned edge;
- gui-winui3/Services/RuAppComposition.cs: production or explicit Harness
  dependency graph;
- gui-winui3/Services/HarnessSessionDescriptor.cs: restricted one-shot
  descriptor parser and deletion boundary;
- gui-winui3/Services/IVoicePlaybackDevice.cs: physical playback boundary;
- gui-winui3/Services/MediaPlayerVoicePlaybackDevice.cs: production
  MediaPlayer implementation moved out of orchestration;
- gui-winui3/Services/RecordingVoicePlaybackDevice.cs: Harness-only recording
  sink selected only by explicit composition;
- gui-winui3/Services/DeterministicVoiceFrameSource.cs: Harness-only frame
  source selected only by explicit composition;
- gui-winui3/Services/HarnessEventSink.cs: bounded safe event counts for the
  external UI Automation driver.

Modify the existing production files:

- gui-winui3/App.xaml.cs: parse the explicit descriptor and construct the
  composition before creating MainWindow;
- gui-winui3/MainWindow.xaml.cs: consume a provider/session instead of always
  starting BackendProcessManager, inject voice factories, and skip subtitle
  overlay creation in deterministic mode;
- gui-winui3/MainWindow.xaml: add stable automation IDs to shell readiness and
  session controls;
- gui-winui3/Services/ApiService.cs, ChatWebSocketClient.cs, and
  VoiceStreamClient.cs: accept BackendSession while retaining compatible
  loopback constructors;
- gui-winui3/Services/VoicePlaybackService.cs: delegate physical audio to
  IVoicePlaybackDevice while retaining stream, VTS acknowledgement, subtitle,
  and cancellation ordering;
- gui-winui3/Views/ChatView.xaml.cs: accept an injected frame-source factory;
- gui-winui3/Views/ChatView.xaml: expose MicButton, voice status, messages, and
  send controls through stable automation IDs.

Create the typed UI Automation driver:

- gui-winui3.Harness/RuApp.Harness.csproj: Windows console project with
  Windows desktop UI Automation support;
- gui-winui3.Harness/Program.cs: argument validation, app launch, bounded
  scenario selection, safe JSON output, and process cleanup;
- gui-winui3.Harness/UiAutomationScenario.cs: AutomationId-based actions and
  state assertions;
- gui-winui3.Harness/HarnessGuiResult.cs: safe GUI check result records;
- scripts/run_gui_voice_harness.ps1: explicit interactive-runner wrapper;
- .github/workflows/gui-harness.yml: manual/self-hosted interactive job only.

Create focused tests:

- gui-winui3.Tests/BackendSessionTests.cs;
- gui-winui3.Tests/RuAppCompositionTests.cs;
- gui-winui3.Tests/HarnessSessionDescriptorTests.cs;
- gui-winui3.Tests/VoicePlaybackDeviceTests.cs;
- gui-winui3.Tests/HarnessEventSinkTests.cs.

Modify these existing tests:

- gui-winui3.Tests/BackendProcessManagerTests.cs for the managed provider;
- gui-winui3.Tests/VoiceConversationServiceTests.cs for session replacement
  and stale reply suppression;
- gui-winui3.Tests/VoiceTurnCaptureServiceTests.cs for deterministic source
  timing and no-device construction;
- gui-winui3.Tests/ChatViewVoiceContractTests.cs for injected factory and
  automation IDs.

## 2. Task 1: Add a validated backend session and external composition seam

**Files:**

- Create: gui-winui3/Services/BackendSession.cs
- Create: gui-winui3/Services/IRuBackendSessionProvider.cs
- Create: gui-winui3/Services/ManagedRuBackendSessionProvider.cs
- Create: gui-winui3/Services/ExternalRuBackendSessionProvider.cs
- Create: gui-winui3/Services/RuAppComposition.cs
- Create: gui-winui3/Services/HarnessSessionDescriptor.cs
- Test: gui-winui3.Tests/BackendSessionTests.cs
- Test: gui-winui3.Tests/RuAppCompositionTests.cs
- Test: gui-winui3.Tests/HarnessSessionDescriptorTests.cs

- [x] **Step 1: Write endpoint and descriptor tests first**

Add tests for the exact validated endpoint behavior:

~~~csharp
[Fact]
public void BackendSession_builds_http_and_websocket_endpoints_from_a_loopback_port()
{
    var session = BackendSession.FromLoopback(18001, "desktop-secret");

    Assert.Equal(new Uri("http://127.0.0.1:18001/"), session.HttpBaseUri);
    Assert.Equal(
        new Uri("ws://127.0.0.1:18001/chat?token=desktop-secret"),
        session.ChatUri);
}

[Theory]
[InlineData("https://example.com")]
[InlineData("http://192.168.1.10:8000")]
public void Harness_descriptor_rejects_non_loopback_edge(string baseUrl)
{
    Assert.Throws<ArgumentException>(() =>
        HarnessSessionDescriptor.Create(baseUrl, "secret", "turn", "events.jsonl"));
}
~~~

Add a composition test with a FakeRuBackendSessionProvider:

~~~csharp
[Fact]
public async Task Harness_composition_returns_external_session_without_starting_children()
{
    var provider = new FakeRuBackendSessionProvider(
        BackendSession.FromLoopback(18001, "secret"));
    var composition = RuAppComposition.ForHarness(provider, "events.jsonl");

    var session = await composition.BackendProvider.StartAsync("D:/LLM", CancellationToken.None);

    Assert.Equal(18001, session!.HttpBaseUri.Port);
    Assert.Equal(0, provider.StartManagedBackendCalls);
    Assert.False(composition.EnableSubtitleOverlay);
}
~~~

- [x] **Step 2: Run the new endpoint tests and verify they fail**

Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --filter "FullyQualifiedName~BackendSessionTests|FullyQualifiedName~RuAppCompositionTests|FullyQualifiedName~HarnessSessionDescriptorTests" --nologo
~~~

Expected: compilation fails because BackendSession, the provider contract, and
the composition types do not exist.

- [x] **Step 3: Implement BackendSession and compatible client construction**

Define:

~~~csharp
public sealed record BackendSession(Uri HttpBaseUri, string AuthSecret)
{
    public Uri ChatUri => BuildWebSocketUri("/chat");
    public Uri VoiceStreamUri => BuildWebSocketUri("/voice/stream");

    public static BackendSession FromLoopback(int port, string authSecret)
        => new(new Uri($"http://127.0.0.1:{port}/"), ValidateSecret(authSecret));

    public Uri BuildWebSocketUri(string path)
        => new($"ws://{HttpBaseUri.Host}:{HttpBaseUri.Port}{path}?token={Uri.EscapeDataString(AuthSecret)}");
}
~~~

Validate that the URI is http, the host is loopback, the port is 1024-65535,
and the secret is non-empty. Change ApiService, ChatWebSocketClient, and
VoiceStreamClient to accept BackendSession; keep their current
(string authSecret, int port = 8000) constructors as delegating overloads so
normal callers and existing tests remain source-compatible. All clients must
derive URLs from the session instead of concatenating a fixed port in multiple
places.

- [x] **Step 4: Implement managed/external providers and composition**

IRuBackendSessionProvider must expose:

~~~csharp
public interface IRuBackendSessionProvider : IDisposable
{
    string DisplayName { get; }
    BackendSession? Session { get; }
    event EventHandler? BackendExited;
    Task<BackendSession?> StartAsync(string projectRoot, CancellationToken cancellationToken);
}
~~~

ManagedRuBackendSessionProvider wraps the existing BackendProcessManager,
calls StartBackendAsync, and returns BackendSession.FromLoopback(manager.Port,
manager.AuthSecret). It forwards BackendExited and disposes the manager.
ExternalRuBackendSessionProvider returns the validated descriptor session,
never creates a process, and has a no-op dispose.

RuAppComposition.Production() must create the managed provider. Its Harness
factory must accept the external provider, a frame-source factory, a playback
device factory, an event sink, and EnableSubtitleOverlay=false. Do not use a
global environment-variable branch inside views.

- [x] **Step 5: Implement the one-shot restricted descriptor**

HarnessSessionDescriptor must contain base_url, auth_token, scenario,
events_path, and voice_mode. LoadFromArgument(string arguments) accepts only
--ru-harness-session <path>, reads UTF-8 JSON, validates loopback URL, allowed
scenario values (turn, stop, cancel, stale-session), voice mode deterministic,
and an events path under the same temporary session root. It deletes the
descriptor immediately after a successful read. It never writes the token to a
diagnostic event. Invalid or missing descriptors fail composition creation
rather than falling back to production mode.

Use Windows ACLs in the PowerShell/Python writer to remove inherited access and
grant the current runner identity full access to the descriptor. The app-side
loader still treats the file as one-shot and removes it in a finally block.

- [x] **Step 6: Run endpoint, composition, and existing manager tests**

Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --filter "FullyQualifiedName~BackendSessionTests|FullyQualifiedName~RuAppCompositionTests|FullyQualifiedName~HarnessSessionDescriptorTests|FullyQualifiedName~BackendProcessManagerTests" --nologo
~~~

Expected: all new tests pass; managed mode still uses port 8000 and the
existing worker-port validation; Harness mode reports no managed child start.

- [x] **Step 7: Commit the composition boundary**

Run:

~~~powershell
git add -- gui-winui3/Services/BackendSession.cs gui-winui3/Services/IRuBackendSessionProvider.cs gui-winui3/Services/ManagedRuBackendSessionProvider.cs gui-winui3/Services/ExternalRuBackendSessionProvider.cs gui-winui3/Services/RuAppComposition.cs gui-winui3/Services/HarnessSessionDescriptor.cs gui-winui3/Services/ApiService.cs gui-winui3/Services/ChatWebSocketClient.cs gui-winui3/Services/VoiceStreamClient.cs gui-winui3.Tests/BackendSessionTests.cs gui-winui3.Tests/RuAppCompositionTests.cs gui-winui3.Tests/HarnessSessionDescriptorTests.cs gui-winui3.Tests/BackendProcessManagerTests.cs
git commit -m "feat: add RuApp external backend composition"
~~~

## 3. Task 2: Separate physical playback from voice orchestration

**Files:**

- Create: gui-winui3/Services/IVoicePlaybackDevice.cs
- Create: gui-winui3/Services/MediaPlayerVoicePlaybackDevice.cs
- Create: gui-winui3/Services/RecordingVoicePlaybackDevice.cs
- Create: gui-winui3/Services/HarnessEventSink.cs
- Modify: gui-winui3/Services/VoicePlaybackService.cs
- Test: gui-winui3.Tests/VoicePlaybackDeviceTests.cs
- Test: gui-winui3.Tests/HarnessEventSinkTests.cs

- [x] **Step 1: Write the failing playback seam tests**

~~~csharp
[Fact]
public async Task Recording_device_records_playback_without_constructing_media_player()
{
    using var events = new InMemoryHarnessEventSink();
    using var device = new RecordingVoicePlaybackDevice(events);

    await device.PlayAsync(BuildValidWav(), CancellationToken.None);

    Assert.Equal(1, device.PlayCalls);
    Assert.Equal(1, device.CompletedCalls);
    Assert.Equal(new[] { "voice.playback.started", "voice.playback.ended" }, events.Codes);
}

[Fact]
public void Event_sink_rejects_audio_transcript_and_secret_fields()
{
    using var events = new InMemoryHarnessEventSink();

    Assert.Throws<ArgumentException>(() => events.Write("voice", new { audio = "bytes" }));
    Assert.Throws<ArgumentException>(() => events.Write("voice", new { transcript = "private" }));
    Assert.Throws<ArgumentException>(() => events.Write("voice", new { token = "secret" }));
}
~~~

BuildValidWav() in the test must create a 16-bit mono 16000 Hz in-memory WAV
with 3200 frames. The first run must fail because the device and event sink do
not exist.

- [x] **Step 2: Run the focused playback tests and verify failure**

Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --filter "FullyQualifiedName~VoicePlaybackDeviceTests|FullyQualifiedName~HarnessEventSinkTests" --nologo
~~~

Expected: compilation fails with missing IVoicePlaybackDevice and
InMemoryHarnessEventSink.

- [x] **Step 3: Define the physical playback boundary**

Add:

~~~csharp
public interface IVoicePlaybackDevice : IDisposable
{
    bool IsPlaying { get; }
    Task PlayAsync(byte[] audio, CancellationToken cancellationToken, Action? onStarted = null);
    void Stop();
}
~~~

Move the existing MediaPlayer, InMemoryRandomAccessStream, opened/ended
completion sources, MediaFailed, Play, pause, and stream disposal logic from
VoicePlaybackService.PlayWavAsync into
MediaPlayerVoicePlaybackDevice. Preserve playback-first ordering: the device
must call MediaPlayer.Play() before invoking onStarted, and cancellation must
dispose the player and stream.

- [x] **Step 4: Implement the recording device and safe event sink**

RecordingVoicePlaybackDevice validates the WAV using RIFF parsing, records only
play/start/end/error counts and bounded elapsed milliseconds, and completes
playback immediately or after the descriptor's bounded delay. It never
references MediaPlayer, AudioGraph, microphone APIs, or VTube Studio.

HarnessEventSink writes only allowlisted event codes and scalar fields to the
descriptor-provided JSONL path. Allow these codes: voice.capture.started,
voice.capture.ended, voice.transcribe.completed, voice.chat.sent,
voice.reply.completed, voice.playback.started, voice.playback.ended,
voice.conversation.stopped, voice.conversation.cancelled, and
voice.session.replaced. Reject keys named token, secret, audio, transcript,
prompt, response, session_id, or request_body; cap the file at 64 KiB and rotate
to an in-memory failure state instead of growing.

- [x] **Step 5: Delegate VoicePlaybackService to the device**

Change the constructor to:

~~~csharp
public VoicePlaybackService(
    ApiService apiService,
    IVoicePlaybackDevice? playbackDevice = null)
{
    _apiService = apiService;
    _playbackDevice = playbackDevice ?? new MediaPlayerVoicePlaybackDevice();
}
~~~

Replace _player/_stream ownership with _playbackDevice. Keep
VoicePlaybackService responsible for voice stream protocol, VTS speaking
acknowledgement, subtitle events, request IDs, cancellation, and latency. Its
IsPlaying forwards to the device; StopVoice calls _playbackDevice.Stop();
its existing PlayWavAsync calls PlayAsync and keeps request acknowledgement
around that call. Dispose the device exactly once.

- [x] **Step 6: Run playback, voice queue, and latency tests**

Run sequentially:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --filter "FullyQualifiedName~VoicePlaybackDeviceTests|FullyQualifiedName~VoicePlaybackQueueTests|FullyQualifiedName~VoiceReplyLatencyTrackerTests|FullyQualifiedName~VoicePlaybackSubtitleIsolationTests" --nologo
~~~

Expected: recording playback passes without a device, existing queue ordering
and subtitle isolation remain green, and cancellation does not report a late
playback-ended event.

- [x] **Step 7: Commit the playback seam**

Run:

~~~powershell
git add -- gui-winui3/Services/IVoicePlaybackDevice.cs gui-winui3/Services/MediaPlayerVoicePlaybackDevice.cs gui-winui3/Services/RecordingVoicePlaybackDevice.cs gui-winui3/Services/HarnessEventSink.cs gui-winui3/Services/VoicePlaybackService.cs gui-winui3.Tests/VoicePlaybackDeviceTests.cs gui-winui3.Tests/HarnessEventSinkTests.cs
git commit -m "feat: inject deterministic voice playback boundary"
~~~

## 4. Task 3: Inject deterministic capture and wire MainWindow/ChatView

**Files:**

- Create: gui-winui3/Services/DeterministicVoiceFrameSource.cs
- Modify: gui-winui3/App.xaml.cs
- Modify: gui-winui3/MainWindow.xaml.cs
- Modify: gui-winui3/MainWindow.xaml
- Modify: gui-winui3/Views/ChatView.xaml.cs
- Modify: gui-winui3/Views/ChatView.xaml
- Test: gui-winui3.Tests/VoiceTurnCaptureServiceTests.cs
- Modify: gui-winui3.Tests/VoiceConversationServiceTests.cs
- Modify: gui-winui3.Tests/ChatViewVoiceContractTests.cs

- [x] **Step 1: Write the deterministic-source and composition integration tests**

Add a source test that proves no physical audio object is constructed:

~~~csharp
[Fact]
public async Task Deterministic_source_emits_one_speech_turn_and_stops_cleanly()
{
    using var source = new DeterministicVoiceFrameSource(
        new DeterministicVoiceFrameOptions(
            SpeechDelay: TimeSpan.Zero,
            EndDelay: TimeSpan.FromMilliseconds(10)));
    var frames = new List<short[]>();
    source.FrameReady += samples => frames.Add(samples.ToArray());

    await source.StartAsync(CancellationToken.None);
    await Task.Delay(50);
    await source.StopAsync();

    Assert.NotEmpty(frames);
    Assert.Contains(frames.SelectMany(item => item), sample => sample != 0);
}
~~~

Add a source-factory contract assertion:

~~~csharp
[Fact]
public void Chat_initialize_accepts_a_voice_frame_factory()
{
    string code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));
    Assert.Contains("Func<IVoiceFrameSource>?", code, StringComparison.Ordinal);
    Assert.Contains("new VoiceTurnCaptureService(voiceSourceFactory)", code, StringComparison.Ordinal);
}
~~~

Extend VoiceConversationServiceTests with a session replacement case that
completes an old transcription after StopAsync and verifies zero send, zero
playback, and one voice.session.replaced event.

- [x] **Step 2: Run the focused tests and verify the new assertions fail**

Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --filter "FullyQualifiedName~VoiceTurnCaptureServiceTests|FullyQualifiedName~VoiceConversationServiceTests|FullyQualifiedName~ChatViewVoiceContractTests" --nologo
~~~

Expected: the deterministic source and ChatView.Initialize factory assertion
fail before implementation; all existing capture/state tests remain visible.

- [x] **Step 3: Implement the deterministic frame source**

DeterministicVoiceFrameSource implements the existing IVoiceFrameSource:

- StartAsync schedules one bounded sequence of short frames at 16000 Hz;
- the sequence contains deterministic nonzero samples followed by zero samples
  so the existing VAD ends the turn;
- StopAsync cancels the scheduled sequence and never emits after disposal;
- Faulted is raised only when the configured test scenario explicitly asks for
  a source fault; and
- the class contains no Windows.Media.Audio, AudioGraph, device enumerator, or
  microphone permission call.

Use DeterministicVoiceFrameOptions with SpeechDelay, EndDelay, EmitFault, and
LateFrameDelay; validate all delays to 0-5000 ms. Keep the existing
VoiceTurnCaptureService VAD and WAV encoding path under test rather than
bypassing it.

- [x] **Step 4: Wire ChatView to the injected source factory**

Change the signature to:

~~~csharp
public void Initialize(
    ApiService apiService,
    ChatWebSocketClient wsClient,
    VoicePlaybackService voicePlayback,
    Func<IVoiceFrameSource>? voiceSourceFactory = null,
    HarnessEventSink? harnessEvents = null)
~~~

Construct new VoiceTurnCaptureService(voiceSourceFactory) and write only
allowlisted state events through harnessEvents. Keep transcribe, send,
reply-completion, cancellation, stale-result suppression, and marker stripping
on the existing production path. Do not put event codes, VTS markers, voice
pace fields, or request IDs into Messages or ChatInputTextBox.

Add AutomationProperties.AutomationId values in ChatView.xaml: MicButton,
VoiceConversationStatus, ChatMessages, ChatInput, and SendButton. Add a small
TextBlock named VoiceConversationStatusText; it displays only user-safe state
labels such as Đang nghe..., Đang xử lý..., Đang chờ Ru trả lời..., and Đã dừng.
SetVoiceButtonState and OnVoiceConversationStatusChanged update that state text.

- [x] **Step 5: Wire App and MainWindow to the composition**

In App.OnLaunched, call RuAppComposition.FromArguments(args.Arguments);
production arguments return RuAppComposition.Production(), while a valid Harness
descriptor returns the external provider and deterministic factories. Construct
new MainWindow(composition).

In MainWindow, replace the direct BackendProcessManager field with the
composition's IRuBackendSessionProvider. InitializeAppAsync calls StartAsync;
production starts children as before, Harness mode simply returns the descriptor
session. Build ApiService and ChatWebSocketClient from the returned
BackendSession. Construct VoicePlaybackService with the composition playback
device factory and call ChatView.Initialize with the composition frame-source
factory and event sink.

In Harness mode, do not create or start SubtitleOverlayWindow, do not start the
observation polling timer, and do not report a fake VTS connection as a live
device. Keep the normal timers and overlay behavior unchanged in production
mode. Add AutomationProperties.AutomationId="BackendStatus" to StatusText,
NewSessionButton, and SessionList in MainWindow.xaml.

- [x] **Step 6: Run the focused GUI tests**

Run sequentially:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --filter "FullyQualifiedName~VoiceTurnCaptureServiceTests|FullyQualifiedName~VoiceConversationServiceTests|FullyQualifiedName~ChatViewVoiceContractTests|FullyQualifiedName~BackendProcessManagerTests" --nologo
dotnet build D:\LLM\gui-winui3\RuApp.csproj -c Release -r win-x64 --no-restore --nologo
~~~

Expected: capture, state-machine, stale-result, privacy, manager, and static
automation-ID tests pass; Release build succeeds with no warnings introduced
by the composition seam.

- [x] **Step 7: Commit the GUI composition wiring**

Run:

~~~powershell
git add -- gui-winui3/Services/DeterministicVoiceFrameSource.cs gui-winui3/App.xaml.cs gui-winui3/MainWindow.xaml.cs gui-winui3/MainWindow.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3/Views/ChatView.xaml gui-winui3.Tests/VoiceTurnCaptureServiceTests.cs gui-winui3.Tests/VoiceConversationServiceTests.cs gui-winui3.Tests/ChatViewVoiceContractTests.cs
git commit -m "feat: wire deterministic voice capture into RuApp composition"
~~~

## 5. Task 4: Build the typed UI Automation driver

**Files:**

- Create: gui-winui3.Harness/RuApp.Harness.csproj
- Create: gui-winui3.Harness/Program.cs
- Create: gui-winui3.Harness/UiAutomationScenario.cs
- Create: gui-winui3.Harness/HarnessGuiResult.cs

- [x] **Step 1: Add driver project and pure result tests**

Create a Windows console project targeting
net10.0-windows10.0.26100.0, with UseWPF=true for
System.Windows.Automation, nullable/implicit usings enabled, and no reference
back to the WinUI executable. Add a unit-testable HarnessGuiResult record:

~~~xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
~~~

~~~csharp
public sealed record HarnessGuiCheck(
    string Id,
    string Status,
    double DurationMs,
    string? Reason = null);

public sealed record HarnessGuiResult(
    int SchemaVersion,
    string Scenario,
    IReadOnlyList<HarnessGuiCheck> Checks,
    IReadOnlyDictionary<string, int> EventCounts,
    bool ExistingInstanceDetected,
    bool AppClosed);
~~~

The driver output is safe JSON only; it contains no token, path, transcript,
audio, window title beyond a stable status, or request body.

- [x] **Step 2: Implement bounded AutomationId selectors**

UiAutomationScenario must find AutomationElements by
AutomationElement.AutomationIdProperty, never by screen coordinates or fuzzy
visible text. Implement:

~~~csharp
Task WaitForBackendReadyAsync(TimeSpan timeout);
Task StartVoiceAsync(TimeSpan timeout);
Task WaitForVoiceTurnAsync(TimeSpan timeout);
Task RequestSecondTurnAsync(TimeSpan timeout);
Task StopVoiceAsync(TimeSpan timeout);
Task ReplaceSessionAsync(TimeSpan timeout);
Task AssertNoLateUpdateAsync(TimeSpan duration);
~~~

Each method polls every 50 ms until its deadline and returns a named
HarnessGuiCheck. A missing control, wrong button name, wrong status label, or
timeout is a failure for the exact check ID; it does not throw an unclassified
exception into the artifact.

- [x] **Step 3: Implement the normal deterministic turn scenario**

Program validates --app, --session, --scenario, and --timeout, refuses to run
when the app path is missing, and checks for an existing RuApp process or
window before launch. On a clean desktop it starts the app with only
--ru-harness-session <descriptor-path> and waits for BackendStatus to show
đã sẵn sàng or the Harness-ready equivalent.

For scenario turn, the driver must:

1. click MicButton;
2. assert its accessible name is Dừng trò chuyện bằng giọng nói;
3. assert VoiceConversationStatus becomes Đang nghe...;
4. wait for one capture, transcribe, chat-send, reply-complete, and playback
   ended event in the safe event log;
5. assert ChatMessages contains one user item and one assistant item without a
   private marker; and
6. call RequestSecondTurnAsync, assert gui.voice_relisten, and verify a second
   capture/send/reply/playback sequence; and
7. assert the state returns to Đang nghe... for the next turn.

The driver must close the app through its normal window close path, then use
process-tree cleanup only if the app remains after the bounded close timeout.

- [x] **Step 4: Implement stop, cancellation, and stale-session scenarios**

For scenario stop, click MicButton during the deterministic source delay, assert
Đã dừng, then wait 500 ms and require zero voice.chat.sent and zero
voice.playback.started events.

For scenario cancel, release one frame, stop while STT is delayed, and require
no assistant message, no playback event, and one
voice.conversation.cancelled event.

For scenario stale-session, start a delayed turn, activate NewSessionButton,
assert voice.session.replaced, and require the old result not to update
ChatMessages, VoiceConversationStatus, or playback. The driver returns separate
check IDs gui.voice_stop, gui.voice_cancel, and gui.voice_stale_result.

- [x] **Step 5: Build and run driver-side contract checks**

Run:

~~~powershell
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --filter "FullyQualifiedName~ChatViewVoiceContractTests|FullyQualifiedName~VoiceConversationServiceTests" --nologo
~~~

Expected: the driver builds and its production-side state/automation contract
tests pass. Do not claim UI Automation acceptance until the interactive scenario
has run.

- [x] **Step 6: Commit the typed driver**

Run:

~~~powershell
git add -- gui-winui3.Harness/RuApp.Harness.csproj gui-winui3.Harness/Program.cs gui-winui3.Harness/UiAutomationScenario.cs gui-winui3.Harness/HarnessGuiResult.cs
git commit -m "test: add typed RuApp voice UI Harness driver"
~~~

## 6. Task 5: Orchestrate the GUI run and merge the safe artifact

**Files:**

- Create: scripts/run_gui_voice_harness.ps1
- Modify: scripts/harness/session.py
- Modify: scripts/aspnetcore_compatibility_harness.py
- Modify: scripts/harness/artifacts.py
- Test: tests/test_harness_session.py
- Test: tests/test_ci_contract.py

- [x] **Step 1: Define the GUI callback contract in the backend session**

Add a session method with this exact shape:

~~~python
def run_gui(
    self,
    *,
    app_path: Path,
    driver_project: Path,
    scenario: Literal["turn", "stop", "cancel", "stale-session"],
    timeout_seconds: float,
) -> list[CheckResult]: ...
~~~

It writes a descriptor under the session root, applies the current-user ACL,
starts the typed driver while edge/worker children remain alive, captures only
the driver's safe JSON stdout (64 KiB cap), validates schema_version=2,
converts each GUI check into the artifact check list, and deletes the descriptor
even when the driver exits nonzero. A driver startup error is gui.driver_start;
a nonzero scenario result preserves its check IDs and does not turn into a
generic pass/fail-only message.

- [x] **Step 2: Add deterministic delay controls without changing public routes**

Pass scenario timing through the temporary worker command/environment only:

~~~text
RU_HARNESS_SCENARIO=turn|stop|cancel|stale-session
RU_HARNESS_TRANSCRIBE_DELAY_MS=0|300
RU_HARNESS_VOICE_DELAY_MS=0|100
~~~

The deterministic worker reads only these allowlisted variables, clamps delays
to 0-5000 ms, and does not include them in the artifact. turn uses 0 ms STT and
0 ms voice delay; stop uses 100 ms frame completion; cancel and stale-session
use 300 ms STT delay. No environment from the user's machine is copied into the
descriptor or JSON result.

- [x] **Step 3: Add the PowerShell interactive wrapper**

run_gui_voice_harness.ps1 must require:

~~~text
-AppPath <published RuApp.exe>
-OutputPath <safe artifact JSON>
-DriverProject <RuApp.Harness.csproj>
~~~

It resolves each path, checks the app exists, fails if an existing RuApp window
is found, invokes the Python Harness in GUI mode for the four scenarios in this
order (turn, stop, cancel, stale-session), and exits nonzero when any scenario
or cleanup check fails. It runs each scenario as a fresh app process against a
fresh temporary session and never deletes outside the generated session root.
It writes the final v2 artifact only after child/port/descriptor cleanup has
been evaluated.

Example command:

~~~powershell
& D:\LLM\scripts\run_gui_voice_harness.ps1 -AppPath D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe -DriverProject D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -OutputPath D:\LLM\artifacts\gui\ru-harness-vnext-gui.json
if ($LASTEXITCODE -ne 0) { throw "GUI Harness failed with exit code $LASTEXITCODE." }
~~~

- [x] **Step 4: Add artifact and CI contract checks**

Extend artifact validation so mode accepts gui-deterministic and
full-deterministic, components.gui contains launched and closed, and checks
includes gui.voice_start, gui.voice_turn, gui.voice_relisten, gui.voice_stop,
gui.voice_cancel, gui.voice_stale_result, and gui.private_metadata. Merge only
counts, statuses, durations, and safe reasons from the driver. Reject driver
output containing bearer, transcript, audio, or request-body fields.

Add tests/test_ci_contract.py assertions for a separate GUI workflow with
workflow_dispatch, self-hosted, windows, ru-gui, and the PowerShell wrapper.
Keep the ordinary headless CI job free of UI Automation.

- [x] **Step 5: Run the backend session and script contract tests**

Run:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_artifacts.py tests/test_harness_session.py tests/test_ci_contract.py
~~~

Expected: descriptor cleanup, scenario allowlist, artifact merge, and workflow
contract tests pass. The interactive app scenarios are intentionally not
claimed by this headless command.

- [x] **Step 6: Commit orchestration**

Run:

~~~powershell
git add -- scripts/run_gui_voice_harness.ps1 scripts/harness/session.py scripts/aspnetcore_compatibility_harness.py scripts/harness/artifacts.py tests/test_harness_session.py tests/test_ci_contract.py
git commit -m "test: orchestrate deterministic RuApp voice scenarios"
~~~

## 7. Task 6: Add the explicit interactive workflow and documentation

**Files:**

- Create: .github/workflows/gui-harness.yml
- Modify: docs/operations/verification.md
- Modify: README.md
- Modify: docs/architecture.md
- Modify: tests/test_current_documentation.py

- [x] **Step 1: Add the manual/self-hosted workflow**

Create a workflow triggered only by workflow_dispatch on a runner labelled
self-hosted, windows, and ru-gui. The job checks out the repository, restores
Python/.NET dependencies, publishes RuApp.exe Release win-x64, runs
scripts/run_gui_voice_harness.ps1, and uploads only the safe JSON artifact with
actions/upload-artifact@v4 under ru-harness-vnext-gui. It must not run on an
ordinary headless pull request job.

- [x] **Step 2: Update verification documentation**

In docs/operations/verification.md, add the exact PowerShell command, the
required interactive runner, the four scenarios, stable automation states, and
the distinction between deterministic GUI acceptance and live hardware. State
that deterministic GUI success proves the application lifecycle and protocol
boundaries only; it does not prove microphone capture, audible OmniVoice, or
VTube Studio behavior.

- [x] **Step 3: Update README and architecture documents**

In README.md, add the GUI Harness command after the backend Harness command and
explain that an existing RuApp instance causes a safe failure. In
docs/architecture.md, document the external BackendSession, normal managed
provider, explicit descriptor, deterministic physical audio adapters, and the
fact that production still owns its real process/audio/VTS lifecycle.

- [x] **Step 4: Run documentation and build verification**

Run sequentially:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_current_documentation.py tests/test_ci_contract.py
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo
dotnet publish D:\LLM\gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore --nologo
~~~

Expected: documentation contracts pass, the driver builds, WinUI tests pass,
and the published app exists at the path passed to the wrapper.

- [x] **Step 5: Commit workflow and documentation**

Run:

~~~powershell
git add -- .github/workflows/gui-harness.yml docs/operations/verification.md README.md docs/architecture.md tests/test_current_documentation.py
git commit -m "ci: add explicit interactive GUI voice Harness"
~~~

## 8. GUI plan acceptance gate

Run the live interactive gate only after the published app and driver build
pass:

~~~powershell
& D:\LLM\scripts\run_gui_voice_harness.ps1 -AppPath D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe -DriverProject D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -OutputPath D:\LLM\artifacts\gui\ru-harness-vnext-gui.json
~~~

Expected: four fresh deterministic scenarios pass; gui.voice_start observes
Đang nghe...; the normal turn records one capture, one STT completion, one chat
send, one assistant completion, and one playback end; stop/cancel/stale
session produce no late UI or audio event; private markers are absent from
visible messages and the artifact; children, ports, descriptor, and temporary
roots are clean.

Then run the separate user-owned hardware gate from the verification document:
real microphone, real audible OmniVoice, subtitles, VTube Studio, and stop during
listening/STT/playback. Do not merge that result into the deterministic Harness
pass count.

## 9. Self-review checklist

- [x] Every GUI spec requirement maps to a task: external composition, dynamic
      endpoint injection, fake frame source, recording playback, UI Automation,
      relisten, stop, cancel, session replacement, privacy, cleanup, artifact,
      and live hardware separation.
- [x] BackendSession, IRuBackendSessionProvider, RuAppComposition,
      IVoicePlaybackDevice, HarnessSessionDescriptor, and the AutomationId names
      are consistent across all tasks.
- [x] The plan contains no incomplete marker or unspecified implementation step.
- [x] No staging command includes existing unrelated dirty files.
- [x] The app fails safely when another instance is already open and never
      silently controls the user's desktop.
