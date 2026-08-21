# Ru hands-free voice conversation design

**Status:** Proposed for review
**Date:** 2026-08-21
**Scope:** WinUI 3 Chat voice interaction only

## Problem

The new WinUI Chat already has the individual pieces needed for voice:

- `MicRecorderService` records bounded 16 kHz mono WAV audio.
- `/voice/transcribe` sends the clip to the existing STT worker.
- `VoicePlaybackService` streams OmniVoice reply audio and keeps subtitles/VTube
  Studio playback on the existing isolated path.

The current Chat microphone action is still a one-shot dictation tool: it puts
the transcript into the text box and waits for a separate Send action. The old
desktop path also had this manual-send seam, but it combined microphone input
with automatic spoken replies. That combination is what is missing from the
new GUI's conversational feel.

## Goal

Add a temporary, user-controlled **voice conversation mode** to WinUI Chat:

1. Click the existing microphone button once to start the mode.
2. Ru listens for one spoken turn and detects the end of speech.
3. The captured turn is transcribed and sent automatically to the active chat
   session.
4. Ru streams the reply through the existing `VoicePlaybackService`.
5. After the reply finishes, Ru listens for the next turn automatically.
6. Click the microphone button again, switch sessions, close the view, or send
   a typed message to stop the mode.

This is half-duplex turn-taking, not simultaneous full-duplex audio. The
microphone stays closed while Ru speaks so the next STT request does not hear
Ru's speaker output and create a feedback loop.

## Non-goals

- No local conversational model, training corpus, evaluator, or model route is
  restored.
- No new backend route or real-time audio transport is introduced. The existing
  `/voice/transcribe`, `/chat`, and `/voice/stream` contracts remain in use.
- No raw audio, transcript diagnostics, or private voice/VTS control metadata
  is persisted.
- No barge-in while Ru is speaking. Interruptible playback can be added later
  as a separate feature after the basic loop is reliable.
- No persistent always-on microphone permission. The user must explicitly start
  each conversation-mode session.

## User experience

### Entry and visible states

The existing Chat microphone button remains the entry point. Its accessible
name, tooltip, icon, and nearby status text reflect the following states:

| State | User-visible behavior | Mic button action |
| --- | --- | --- |
| Idle | The typed composer remains available. The primary mic action starts the voice conversation. | Start voice conversation. |
| Listening | Shows that Ru is listening for the user's turn. | Stop the conversation. |
| Transcribing | Shows that the turn is being recognized. | Cancel the current turn and stop. |
| Sending | Shows that the recognized turn is being sent. | Cancel the conversation. |
| Ru speaking | Shows that Ru is replying by voice. | Stop playback and stop the conversation. |
| Error | Shows a short actionable message without putting internal metadata in Chat. | Return to Idle. |

The normal typed composer remains usable when the mode is idle. The primary mic
button intentionally changes from the current one-shot dictation action to the
conversation-mode entry point; it must not silently send a one-shot transcript
without the conversation loop. A typed Send while the mode is active first
cancels the voice session, then sends the typed message through the existing
path. Voice status text is UI state only; it must never be appended to Chat
history or sent to the backend as a prompt.

### Turn detection

The capture layer uses native Windows audio frames and a testable energy-based
voice activity detector:

- target format is 16 kHz, mono, 16-bit PCM;
- retain a short pre-roll so the first consonant is not clipped;
- require a short voiced window before accepting a turn;
- end a turn after a bounded silence window;
- reject a turn with no voiced audio and return to Listening;
- enforce the existing 60-second maximum per turn;
- always allow the user to stop the session manually.

The exact thresholds are constants in the capture/VAD layer, not scattered in
XAML event handlers. They must be tuned against a real microphone during manual
acceptance, while unit tests cover the state transitions and boundary math.

### Automatic send and reply handoff

Only a non-empty, successfully transcribed turn is sent. The voice controller
owns a per-session cancellation token and a single in-flight turn so a stop,
session switch, or backend reconnect cannot produce a duplicate prompt.

The existing Chat WebSocket remains the source of visible text and session IDs.
The existing `VoicePlaybackService` remains the source of reply audio. The
conversation controller waits for the same reply-completion boundary that
ensures all queued audio has finished before opening the microphone for the next
turn. If auto-read replies are disabled, the controller uses the Chat `done`
boundary instead of waiting for audio.

## Runtime design

Introduce a testable `VoiceConversationService` (or equivalent controller)
between `ChatView` and the existing capture/playback services. It coordinates
state and cancellation but does not own Chat history or backend contracts.

```text
MicButton
   -> VoiceConversationService
      -> voice turn capture + VAD
      -> ApiService.TranscribeAudioAsync
      -> ChatView.SendVoicePromptAsync(activeSessionId)
      -> existing Chat WebSocket streaming
      -> VoicePlaybackService reply-completion boundary
      -> next capture turn
```

The capture implementation should expose an interface/fake seam for tests and
use the Windows audio stack already available to the WinUI target. Do not add a
third-party audio processing library solely for this feature. The existing
`MicRecorderService` can be split or adapted so the old one-shot dictation
action remains supported without duplicating microphone ownership.

The controller must enforce these invariants:

- one active conversation session per ChatView;
- one capture and one STT request per turn;
- no automatic send for an empty or failed transcription;
- stop is idempotent and releases the microphone promptly;
- cancellation prevents a late STT result from sending after stop;
- session changes cancel the old session before a new session can listen;
- a reply error ends the loop with a recoverable UI error;
- a TTS failure never removes or corrupts the visible text reply;
- no voice/VTS markers, correlation IDs, timing data, or status text enter the
  prompt, Chat display, history, TTS, memory, or logs.

## Failure behavior

- Microphone permission/device failure: stop the session and show a concise
  permission/device instruction.
- No speech before the silence/max-turn boundary: show “Không nghe rõ, thử
  lại nha.” and return to Listening without sending an empty prompt.
- STT unavailable or invalid: keep Chat unchanged, show the failure, and stop
  the session rather than looping on a broken worker.
- Chat WebSocket disconnect/error: end the session and preserve the existing
  Chat error rendering.
- OmniVoice unavailable: keep the visible assistant reply, report the voice
  failure through the existing voice path, and stop the session.
- User stop/cancellation: stop capture, cancel pending STT/send/playback work,
  clear only transient voice status, and leave completed Chat messages intact.

## Verification

### Automated

Add WinUI tests for:

- VAD voiced/silent frame classification, pre-roll, silence timeout, and max
  turn boundary;
- state transitions for start, empty turn, successful turn, reply completion,
  stop, cancellation, session switch, and failures;
- exactly-once automatic send for one transcript;
- no send after cancellation or after a stale transcription completes;
- automatic re-listening only after the reply completion boundary;
- UI contract coverage for the new status strings and no-emoji/no-private-marker
  policy.

Keep existing voice queue, subtitle isolation, route, backend, and full-project
tests unchanged unless a shared seam requires a focused update.

### Manual acceptance

Automated tests cannot prove microphone hardware, speaker output, or VTube
Studio behavior. The user-run gate must verify at least:

1. Start the mode, speak a Vietnamese turn, and confirm one visible user message
   and one audible Ru reply.
2. Speak a second turn after Ru finishes and confirm the loop continues without
   another click.
3. Pause naturally, speak quietly, and speak in Vietnamese-English mixed turns;
   confirm the first words are not clipped and silence does not create empty
   prompts.
4. Stop during listening, STT, and playback; confirm no late prompt/audio is
   produced and the microphone is released.
5. Switch sessions and send typed text while active; confirm cancellation and
   session routing are correct.
6. With subtitles and VTube Studio enabled, confirm voice onset is not delayed
   by the subtitle path and private markers never appear in Chat or spoken text.

## Documentation impact

After implementation, update the current operational documents rather than
creating a historical duplicate:

- `docs/operations/runtime-behavior.md`: document the explicit, half-duplex
  hands-free mode, bounded capture, cancellation, and privacy boundary.
- `docs/operations/verification.md`: add the automated state-machine gate and
  the separate physical microphone/speaker acceptance checklist.
- `README.md`: mention the new Chat voice-conversation entry point and its
  manual hardware acceptance boundary.
- `docs/architecture.md`: update the RuApp ownership description only if the
  new capture/controller seam changes the current ownership statement.

## Review checklist

- [x] Reuse current STT and streaming voice contracts.
- [x] Keep local model/training retirement intact.
- [x] Keep typed Chat behavior and session routing compatible.
- [x] Bound capture, silence detection, cancellation, and payload size.
- [x] Keep voice/VTS metadata private.
- [x] Separate deterministic tests from live hardware acceptance.
- [ ] Confirm that half-duplex turn-taking matches the requested “như người
  thực” experience.
- [ ] Confirm that replacing the primary one-shot mic action with conversation
  mode is acceptable; a separate dictation action is out of this scope.
- [ ] Confirm UI wording/status presentation before implementation.
