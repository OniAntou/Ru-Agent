# Ru offline STT proof-of-concept design

## Goal

Add a selectable, local-first speech-to-text boundary to Ru without changing
the default microphone experience or competing with the active LLM and Gwen
TTS model for the 6 GB GPU. The first candidate is Sherpa-ONNX; the existing
Google recognizer remains the default and safe fallback.

## Scope

- Introduce a small `SpeechRecognizer` interface that accepts captured audio
  and returns normalized text or a typed, user-safe failure.
- Move the current Google recognition call behind a `GoogleSpeechRecognizer`
  adapter so existing behaviour remains intact.
- Add a `SherpaOnnxRecognizer` adapter that is optional: importing Ru must not
  require Sherpa-ONNX, and selecting it must explain how to configure a local
  model if the package or model files are absent.
- Persist only the selected recognizer id and a local model directory in GUI
  settings; no microphone recording, transcript, key, or model data is stored.
- Add a narrow settings control for recognizer selection and local model path.
  The default stays `Google` until the offline engine passes the manual gate.
- Add a CLI benchmark that consumes caller-provided WAV files plus their
  expected transcripts. It records final-transcript latency, selected engine,
  success/failure, and normalized transcript; it never downloads a model or
  sends local audio to a new service.

## Non-goals

- No replacement of Gwen, no TTS engine installation, and no Tauri rewrite.
- No automatic model download, background microphone recording, wake word,
  diarization, word timestamps, or cloud account setup.
- No change to the local LLM/Gwen GPU lifecycle. The first Sherpa POC is
  CPU-first; a GPU build is a later benchmark decision, not a default.

## Architecture

`gui.py` continues to own explicit microphone capture and its timeout limits.
It passes the captured `SpeechRecognition.AudioData` to the selected adapter.
The Google adapter delegates to the existing `recognize_google` call. The
Sherpa adapter converts input to 16 kHz mono PCM in memory, invokes a locally
configured Sherpa-ONNX recognizer, and returns its final text.

Recognizer configuration is represented by an immutable value object and is
validated before invocation. A small registry maps the persisted engine id to
an adapter factory. The GUI handles all adapter exceptions in one place and
shows a concise recovery message; it does not expose Python tracebacks or
silently send an offline request to Google.

The benchmark imports the same registry and emits one JSON record per input so
both engines are measured through the production boundary. It is separate from
the GUI and consumes only files explicitly passed by the user.

## Failure handling and privacy

- Missing optional package, incomplete model directory, malformed WAV, timeout,
  and empty transcript each receive distinct actionable messages.
- A selected offline engine never falls back to Google silently. The user must
  choose Google again, preserving the local-first privacy expectation.
- Captured microphone audio remains in memory for the current recognition
  request. The benchmark reads explicitly supplied WAV files and writes no
  audio.
- VTube metadata filtering and chat-memory extraction are unaffected because
  recognition results flow into the existing input box rather than directly
  into memory or TTS.

## Acceptance checks

- New unit tests, not restored legacy tests, cover registry selection, the
  existing Google path, missing Sherpa dependency, invalid local configuration,
  normalized successful offline output, and error mapping.
- New GUI-focused tests prove the selected engine configuration is persisted
  without an API key or transcript and that the default remains Google.
- The benchmark is tested with a fake recognizer and reports timing plus result
  records without downloading models.
- Manual hardware gate uses at least 20 supplied short Vietnamese, English, and
  code-switched samples. Sherpa is adopted as default only when it produces
  clearly better practical recognition than Google on those samples, completes
  within the agreed interaction latency, and does not destabilize LLM or Gwen
  voice playback.

## Deferred follow-up

Only after this STT gate, run an isolated TTS comparison of Gwen, OmniVoice,
and GPT-SoVITS using the same authorized reference recording and bilingual Ru
phrases. A candidate must pass voice identity, Vietnamese pronunciation,
Vietnamese-English switching, cold/warm latency, and GPU lifecycle checks
before it can replace Gwen.
