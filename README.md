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

`RuBackend.exe --managed-runtime` (the ASP.NET Core Public Host) owns the
Worker/Broker lifecycle in one Windows kill-on-close Job Object. `RuApp.exe`
and the headless `RuAgent.Cli` tracer start-or-attach the Public Host through
the shared `RuAgent.Client` (`PublicHostStarter`/`PublicHostLocator`), which
validates an ACL-restricted loopback descriptor (`protocol_version`, `base_url`,
bearer, PID/start-time/nonce) and `GET /status` bearer. On first start the
Public Host creates per-run secrets, starts the private Runtime Worker on
loopback port 8005 via the shared `ru_runtime.worker.server` multiplexer
(`/worker/v1` Chat and `/worker/v2` one-step Agent), starts the isolated
`RuAgentBroker` on a dynamic loopback port, binds the ASP.NET Core Public Host
on port 8000, waits for public readiness, and assigns Worker/Broker to one Job
Object. Idle Public Host exits after 10 minutes without an active Agent run,
approval, or long-lived Chat WebSocket/Agent SSE lease. Broker descriptors and
grants are session-scoped and removed during shutdown.

ASP.NET Core is the only public backend. It owns authentication, validation,
settings and credentials, sessions/history, activity, structured memory, chat
ordering, public errors, durable Agent harness (Core + SQLite
`agent_harness.db` + AES-GCM payloads, conversation outbox, workspace trust,
session permissions, encrypted policy snapshots), and all 54 HTTP plus
2 WebSocket routes (41 pre-existing plus 13 additive `/agent/v1/*`).
Agent v2 (`/worker/v2` one-step `AssistantStep` and `/agent/v1/*` 13-route
API/SSE with bearer auth, `id:<sequence>`/`Last-Event-ID` cursor, idempotency
409, `workspace_busy` 409, `preset_mismatch` 409) is additive and
feature-flagged behind `RuBackend:AgentHarness:Version` (default `v1`);
policy/approval endpoints (`GET`/`POST`/`DELETE /agent/v1/policies`, `POST
/agent/v1/runs/{runId}/approvals/{approvalId}` with first-writer-wins
`approval_stale` and exact scope preview) are active with durable
encrypted-at-rest snapshots and global interactive header
`X-Interactive-Confirmed: true`; `POST /agent/v1/runs/{runId}/reconcile`
is active on the headless Agent v2 path and resolves an ambiguous dispatch
outcome before the run continues.
Python is an authenticated private implementation boundary for model generation
(one-step Worker v2 plus retained v1 Chat), provider adapters, STT execution,
VTube Studio, observation, and the current voice engine coordination. The CLI
tracer `RuAgent.Cli` is a thin headless client: it delegates start/attach to
`RuAgent.Client` and all run behavior to authenticated HTTP/SSE.

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
- In the WinUI shell, Chat and Voice are the primary navigation surfaces. Log,
   Diagnostics, Policy, and Audit are grouped under `Công cụ`; Coding Agent is
   selected from the compact mode pill inside the Chat composer. That mode
   dropdown contains only Chat and Agent; a separate Agent configuration
   button holds workspace and preset choices. The workspace field is optional:
   when empty, the UI uses the known project root while the authenticated API
   still validates the canonical path. Agent prompts use the
   authenticated `/agent/v1/*` API/SSE path, while ordinary Chat keeps the
   Chat WebSocket path. The composer shows bounded Agent status, workspace
   trust, approval, Stop, and Continue controls without placing tool payloads
   in the Chat transcript.
- Tool capability is an explicit tri-state selector. Chat (default) advertises
  only workspace discovery/read (`files.list`, `files.glob`, `files.search`,
  `files.read` with offset/limit, streaming enumeration, hidden/binary/
  protected handling, bounded `truncated` metadata) and declines volume
  commands with guidance. Agent adds `files.write`, `files.edit`,
  `notes.create` and card-free `system.volume` with verified readback. The
  headless Agent v2 worker also advertises `terminal.run` and `terminal.shell`;
  both are policy-evaluated and require explicit approval before execution.
  `files.apply_patch` is not advertised or model-usable in the current runtime.
  Standard automatically allows
  ordinary trusted-workspace reads, creates and edits. An explicit absolute
  path outside the project asks first with `external_directory_required`; the
  user can grant only that intent, the canonical directory for the run, or a
  project-scoped directory rule. Delete, protected paths, declared network use,
  and unknown operations retain their own approval gates;
  Full Access changes fallback decisions but never bypasses workspace trust
  or protocol invariants and remains a current-Windows-user boundary. Allow
  once/run/project and Deny are durable; rule/snapshot replacement follows
  approved old-snapshot execution ordering with Broker as authoritative
  evaluator and Core persisting `tool.execution_requested` before dispatch.
  Current model-usable file tools are `files.list`/`files.glob`/
  `files.search`/`files.read` and hash-guarded `files.write`/`files.edit`
  (`expected_sha256` `missing` or 64-hex), plus `files.delete` (recoverable cap 1 MiB, opaque
  `recovery_id`) and one-time `files.restore` (`recovery_consumed` on replay).
  Every mutation uses preimage checks, atomic same-volume replacement where
  supported, before/after hashes, bounded diff summary, run attribution, and
  recoverable semantics. CLI exposes `agent approve --scope once|run|project`,
  `agent deny`, `agent policy list`/`import --global FILE`/`remove`
  (versioned JSON, `X-Interactive-Confirmed: true`, `--json` cannot bypass),
  and `agent run --preset full` interactive warning (`--json` cannot confirm).
  Terminal (`terminal.run`/`terminal.shell`) is available only on the headless
  Agent v2 path, under policy and per-request approval; it runs as the current
  Windows user and can execute host code or open sockets. The managed Git commit
  workflow (Phase 3 Task 6) is not shipped yet. Modes reset to Chat on every launch and Full
  Access needs one explicit confirmation per run. WinUI displays typed
  activity from actual Host events, and a success is visible only after a
  matching verified Broker receipt.
- Screen observation is opt-in from Settings. When a Gemini Vision key and a
  supported hosted generation backend are configured, the private worker
  captures bounded JPEG frames and publishes reaction events. WinUI consumes
  those events through the normal voice, subtitle, and VTS playback pipeline;
  the worker never plays observation audio directly.

See [runtime behavior](docs/operations/runtime-behavior.md) for invariants and
manual acceptance boundaries.
See [Agent tools and truthful execution](docs/operations/agent-tools.md) for
the exact current capability matrix.

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

The backend artifact covers 54 HTTP routes and 2 WebSocket routes (41 legacy
plus 13 `/agent/v1/*`), requires correct-bearer success plus
wrong/missing-bearer rejection, and records observation routes as explicit
hardware/external skips. It uses synthetic STT/TTS and records only statuses,
timings, counts, event types, and cleanup state. In deterministic mode it also
requires one intentionally invalid `system.volume` request to end as
`failed/invalid_arguments` before any native endpoint is opened. Broker
approval/replay is checked separately with a temporary `notes.create` fixture.
`--deterministic-chat` remains an alias for the backend mode. At this
checkpoint the harness also covers the additive Agent v2 tracer
(HTTP 201/200/409 idempotency, `workspace_busy`, `preset_mismatch`,
unsupported capabilities return bounded `phase_not_available` errors, while
SSE reconnect
`id:<sequence>`/`Last-Event-ID` cursor, encrypted payload binding,
durable `agent_harness.db` policy snapshots/workspace trust/session
permissions, and first-writer-wins approval) via the temp Harness store and
fake Worker/Broker, plus deterministic file-tool matrix
(`files.list`/`glob`/`search`/`read`/`write`/`edit`/`delete`/`restore`; `files.apply_patch`
is intentionally not model-usable) with hash preimage and recovery caps) using a temp
workspace.

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
`agent-volume-contract`, `agent-cancel`, `agent-stale-result`, and
`agent-private-metadata` in addition to the voice scenarios. Broker approval
replay and the production approval-card UI are separate deterministic tests;
they are not model-visible GUI Harness tools. Deterministic GUI success does
not replace the separate live hardware acceptance gate. Cancel and stale-result
replace an in-flight Agent session and reject any delayed visible update into
the replacement session.

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
