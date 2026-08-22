# Ru

Ru is a Vietnamese-English conversational companion built around a WinUI 3
desktop app, an ASP.NET Core public backend, and a private Python runtime
worker. Conversational generation is cloud-first; the repository no longer
ships an in-process conversational model or training pipeline.

Current maintainer documentation:

- [Architecture](docs/architecture.md)
- [Domain language](CONTEXT.md)
- [Runtime behavior](docs/operations/runtime-behavior.md)
- [Verification](docs/operations/verification.md)
- [Architecture decisions](docs/adr/)

## Runtime topology

`RuApp.exe` owns the process lifecycle. It creates per-run secrets, starts the
private Runtime Worker on loopback port 8005, starts the isolated
`RuAgentBroker` on a dynamic loopback port, starts the ASP.NET Core Public Host
on port 8000, waits for public readiness, and places all owned children in a
Windows kill-on-close Job Object. The Broker descriptor and grants are
session-scoped and are removed during shutdown.

ASP.NET Core is the only public backend. It owns authentication, validation,
settings and credentials, sessions/history, activity, structured memory, chat
ordering, public errors, and all 41 HTTP plus 2 WebSocket routes. Python is an
authenticated private implementation boundary for model generation, provider
adapters, STT execution, VTube Studio, observation, and the current voice
engine coordination.

The canonical route inventory is
[`backend-dotnet/public-route-contract.json`](backend-dotnet/public-route-contract.json).
There is no public Python HTTP fallback or alternate backend mode.

## Setup

Use the repository's Python 3.13 environment and install the project extras:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pip install -e "D:\LLM[runtime,dev]"
```

The active local voice assets stay outside Git: `models\omnivoice` and
`omnivoice_env`. Ru does not download, start, or train a local conversational
model.

## Build and run

Build the self-contained Windows executables:

```powershell
& D:\LLM\build.bat
```

Run the desktop-owned stack:

```powershell
& D:\LLM\start_dev.bat
```

These commands produce and start `RuApp.exe` plus `RuBackend.exe`. Do not start
the Runtime Worker or voice engine as independent public services.

## Current behavior

- Chat preserves bounded context, bilingual routing, natural Vietnamese
  `anh`/`em`, streamed visible text, and private control-metadata filtering.
- Hosted OpenCode Zen and Kilo Gateway adapters are optional. OpenAI-compatible
  external adapters remain available for explicitly configured services.
  Credentials are stored through Windows Credential Manager.
- Structured memory and reviewed examples reject credentials, URLs, sensitive
  text, and VTube Studio metadata.
- OmniVoice is the local TTS engine. The accepted quality baseline is the
  original voice at native 1.00x speed; non-1.00x native speed still requires
  listening acceptance.
- In Chat, click the microphone once to start an explicit half-duplex voice
  conversation. Ru detects each bounded turn locally, sends it through the
  existing Google `vi-VN` STT path, submits a non-empty transcript
  automatically, waits for the current reply audio to finish, and listens for
  the next turn. Click the microphone again to stop; the typed composer remains
  available when voice mode is idle.
- VTube Studio actions and lip sync remain private runtime metadata.
  `VoiceVolume` is the mouth input; `LLM_MouthOpen` is not used.
- Agent mode is an explicit session toggle. It gives Ru bounded workspace,
  system-read, and allowlisted terminal access through the private Broker;
  tool activity is exposed as safe status events, never as raw tool arguments
  or credentials. Raw shell, network, web, and external/destructive actions
  remain approval-gated or denied by policy.
- Screen observation is opt-in from Settings. When a Gemini Vision key and a
  supported hosted generation backend are configured, the private worker
  captures bounded JPEG frames and publishes reaction events. WinUI consumes
  those events through the normal voice, subtitle, and VTS playback pipeline;
  the worker never plays observation audio directly.

See [runtime behavior](docs/operations/runtime-behavior.md) for invariants and
manual acceptance boundaries.

## Verification

Run heavyweight gates sequentially. The deterministic backend Harness owns a
temporary worker/Broker/edge session with dynamic loopback ports and emits a safe
schema-v2 artifact:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output D:\LLM\artifacts\aspnetcore\ru-harness-vnext.json
cmd /c D:\LLM\build.bat
```

The backend artifact covers 41 HTTP routes and 2 WebSocket routes, requires
correct-bearer success plus wrong/missing-bearer rejection, and records
observation routes as explicit hardware/external skips. It uses synthetic
STT/TTS and records only statuses, timings, counts, event types, and cleanup
state. In deterministic mode it also exercises the structured Agent loop and
the Broker `files.list` boundary. `--deterministic-chat` remains an alias for
the backend mode.

The deterministic GUI voice Harness is manual and interactive on Windows. It
publishes the real WinUI shell, injects deterministic capture/playback at the
physical boundaries, runs the typed UI Automation driver for turn/stop/cancel/
stale-session scenarios, and writes a separate schema-v2 artifact:

~~~powershell
dotnet publish D:\LLM\gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --nologo
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --nologo
& D:\LLM\scripts\run_gui_voice_harness.ps1 -AppPath D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe -DriverProject D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -OutputPath D:\LLM\artifacts\gui\ru-harness-vnext-gui.json
~~~

It refuses an existing RuApp instance and never opens physical microphone,
speaker, or VTube Studio devices. The driver accepts `--scenario` values
`agent-read-only`, `agent-terminal-approval`, `agent-terminal-reject`,
`agent-cancel`, `agent-stale-result`, and `agent-private-metadata` in addition
to the voice scenarios. Deterministic GUI success does not replace the
separate live hardware acceptance gate.

The production live voice gate is separate and must be run with the real
runtime/audio path:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode runtime-live --require-voice-audio --benchmark-samples 20
```

The logging latency gate can be run independently; it uses temporary activity
and diagnostic roots and requires p95 and p99 overhead to stay within both
2 ms and 2% for every synthetic pipeline path:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\benchmark_logging.py --samples 30
```

Automated tests do not establish speaker quality, physical-microphone accuracy,
or live VTube Studio behavior. The hands-free loop's microphone, speaker,
subtitle, and VTube Studio checks remain user-run hardware acceptance.

## Data and privacy

Business persistence lives in the canonical `%LOCALAPPDATA%\Ru` location
(settings, sessions, history, activity, and the VTS token). Structured memory
stays project-owned under `data/conversations`. `RU_DATA_ROOT` isolates both
for tests and staging. The transactional migration from the legacy
application-data location completed on 2026-08-17; see
[ADR 0004](docs/adr/0004-canonical-ru-data-location.md).
Existing data is protected and must not be manually moved or deleted.

Visible chat text may cross the private generation boundary. Credentials,
audio bytes, VTS actions, voice timing/control metadata, and transcripts must
not enter logs, cleanup manifests, memory, history, or reviewed examples.

## Project boundaries

Model weights, voice assets, active datasets, credentials, runtime app data,
and local caches are not source files. Do not commit them. Do not treat a model
download, unit-test pass, benchmark score, or historical report as live quality
acceptance.
