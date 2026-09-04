# Ru

Ru is a Vietnamese-English conversational companion built around a WinUI 3
desktop app, an ASP.NET Core public backend, and one managed C# Worker serving
Agent and runtime/device boundaries. Conversational generation is cloud-first;
repository no longer ships an in-process conversational model or training
pipeline.

Current maintainer documentation:

- [Architecture](docs/architecture.md)
- [Domain language](CONTEXT.md)
- [Runtime behavior](docs/operations/runtime-behavior.md)
- [Agent tools](docs/operations/agent-tools.md) (including bundled `help.docs`)
- [Verification](docs/operations/verification.md)
- [Architecture decisions](docs/adr/)

## Runtime topology

`RuBackend.exe --managed-runtime` (the ASP.NET Core Public Host) owns the
Worker/Broker lifecycle in one Windows kill-on-close Job Object. `RuApp.exe`
and the headless `RuAgent.Cli` tracer start-or-attach the Public Host through
the shared `RuAgent.Client` (`PublicHostStarter`/`PublicHostLocator`), which
validates an ACL-restricted loopback descriptor (`protocol_version`, `base_url`,
bearer, PID/start-time/nonce) and `GET /status` bearer. On first start the
Public Host creates separate per-run Agent and Device Worker secrets, starts the
 managed C# Worker on loopback ports 8005 (`/worker/v2`) and 8006
 (`/worker/v1`), starts the isolated `RuAgentBroker` on a
dynamic loopback port, binds the ASP.NET Core Public Host on port 8000, waits
for public readiness, and assigns all children to one Job Object. The Host
remains running until explicitly stopped and does not auto-exit on idle.
Broker descriptors and
grants are session-scoped and removed during shutdown.

ASP.NET Core is the only public backend. It owns authentication, validation,
settings and credentials, sessions/history, activity, structured memory, Agent
run ordering, public errors, durable Agent harness (Core + SQLite
`agent_harness.db` + AES-GCM payloads, conversation outbox, workspace trust,
session permissions, encrypted policy snapshots), and all 64 HTTP plus
1 WebSocket route (`/voice/stream`; 43 non-Agent HTTP routes plus 20 additive
`/agent/v1/*` plus 1 `POST /host/shutdown`).
The public Agent path uses Worker v2 (`/worker/v2` one-step `AssistantStep` and
`/agent/v1/*` 20-route API/SSE with bearer auth, `id:<sequence>`/
`Last-Event-ID` cursor, idempotency 409, `workspace_busy` 409,
`preset_mismatch` 409). The legacy `RuBackend:AgentHarness:Version` setting is
still accepted by compatibility Harness construction, but it is not a product
mode switch and cannot re-enable Chat. Policy/approval endpoints (`GET`/`POST`/`DELETE /agent/v1/policies`, `POST
/agent/v1/runs/{runId}/approvals/{approvalId}` with first-writer-wins
`approval_stale` and exact scope preview) are active with durable
encrypted-at-rest snapshots and global interactive header
`X-Interactive-Confirmed: true`; `POST /agent/v1/runs/{runId}/reconcile`
is active on the headless Agent v2 path and resolves an ambiguous dispatch
outcome before the run continues.
The managed C# Worker owns authenticated Agent model steps (Worker v2) and the
v1 runtime/device boundary for settings, voice orchestration, STT, VTube Studio,
observation, and lifecycle coordination. The production Python Agent + Device
topology has been retired; the deterministic Harness uses a dependency-free
test fixture instead.
The CLI tracer `RuAgent.Cli` is a thin headless client: it delegates
start/attach to `RuAgent.Client` and all run behavior to authenticated
HTTP/SSE.

The canonical route inventory is
[`backend-dotnet/public-route-contract.json`](backend-dotnet/public-route-contract.json).
There is no public Python HTTP fallback or public backend mode switch;
the retired `PythonCompatibility` selector fails closed with the stable
`python_compatibility_retired` code. The retired `runtime-live` diagnostic
reports the same bounded gate and never starts a Python or partial Worker
topology.

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

`build.bat` publishes the canonical self-contained Release GUI at
`gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe`
and the managed Agent Worker at
`backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Worker.exe`;
it stops with an error if either artifact is missing or if the GUI runtimeconfig
does not prove a self-contained publish. `start_dev.bat` launches only that
Release GUI after confirming both artifacts exist, preserves `D:\LLM` as the
working directory, and reports an immediate startup failure instead of closing
silently. It never falls back to a Debug build, so a successful build must
reach `[13/13] Build complete!` before using a shortcut. These commands produce and start `RuApp.exe` plus
`RuBackend.exe`; the backend owns the managed C# Worker on both private ports.
Do not start either worker or the voice engine as independent public services.

## Current behavior

- Agent preserves bounded context, bilingual routing, natural Vietnamese
  `anh`/`em`, streamed visible text, private control-metadata filtering, and
  correlation-aware FIFO turns that drop stale socket frames instead of
  attaching them to a newer bubble.
- ASP.NET Core owns persisted sessions and history. A session deletion is
  successful only when the API envelope returns application status `"ok"`; the
  WinUI client checks that status before refreshing the sidebar and shows a
  bounded error when deletion fails. Deleting the last session successfully
  creates one fresh session.
- Hosted OpenCode Zen and Kilo Gateway adapters are optional. OpenAI-compatible
  external adapters remain available for explicitly configured services.
  Credentials are stored through Windows Credential Manager.
- Structured memory and reviewed examples reject credentials, URLs, sensitive
  text, and VTube Studio metadata.
- OmniVoice is the local TTS engine. The accepted quality baseline is the
  original voice at native 1.00x speed; non-1.00x native speed still requires
  listening acceptance.
- Click the microphone once to start an explicit half-duplex voice
  conversation. Ru detects each bounded turn locally, sends it through the
  existing Google `vi-VN` STT path, submits a non-empty transcript
  automatically, waits for the current reply audio to finish, and listens for
  the next turn. Click the microphone again to stop; the typed composer remains
  available when voice mode is idle.
- VTube Studio actions and lip sync remain private runtime metadata.
  `VoiceVolume` is the mouth input; `LLM_MouthOpen` is not used.
- In the WinUI shell, conversation and Voice are the primary navigation surfaces.
  Log, Diagnostics, Policy, and Audit are grouped under `CÃ´ng cá»¥`; the composer
  shows one static Agent indicator. Agent is the only generation mode and the
  Agent session is **session-scoped** — no preset picker or scope picker is
  exposed. The UI always uses the known project root as the initial Agent
  workspace; workspace selection is not exposed in the composer, while the
  authenticated API still validates the canonical path. If the app has not
  opened a session yet, the first Agent prompt creates a fresh session
  before applying the broker's effective profile. All prompts use the
  authenticated `/agent/v1/*` API/SSE path. The composer shows bounded
  Agent status, approval, Stop, and Continue controls; the conversation
  transcript can show a transient safe activity feed for the current run.
  The feed is separate from durable messages and never places raw tool
  payloads in the transcript. The inline approval panel renders one
  bounded message per reason code —
  `external_directory_required`, `terminal_delete_requires_approval`,
  or `terminal_unknown_executable` — and the user never picks a scope.
- Multimodal image attachments: the composer accepts image pasting and file
  attachments (PNG, JPEG, WebP, max 4 images, auto-resized on client up to
  2048px / JPEG 0.85). ASP.NET Core validates payload bounds (4 MB per image,
  20 MB total decoded) and persists encrypted SQLite `agent_payloads` of kind
  `UserImage` (value 11). Worker v2 projects images into `ModelRequest.Images`,
  which provider adapters map to native image blocks (`input_image` for OpenAI
  Responses, `image` for Anthropic Messages, `image_url` for OpenAI-Compatible).
  Models without vision capability fail closed with `vision_unsupported`. Message
  bubbles render thumbnails up to 240px and provide click-to-zoom preview.
- The current Worker v2 model surface has eleven core bindings in both presets (including offline `help.docs`):
  `files.list`, `files.glob`, `files.search`, `files.read`, `files.write`,
  `files.edit`, `files.delete`, `files.restore`, `terminal.run`,
  `terminal.shell`, and `help.docs`, plus the managed Agent's read-only `web.search` and
  `web.fetch` bindings. Standard automatically allows ordinary trusted-workspace
  file reads/creates/edits and applies analyzer/rule/approval decisions to
  terminal calls. Full Access keeps the same advertised list but normally uses
  its broader fallback for well-formed calls; protected paths, hard invariants,
  and extended timeout thresholds remain gated. `files.apply_patch` is not
  advertised or model-usable in the current runtime.
  Explicit absolute paths outside the project ask with
  `external_directory_required`; the user can grant only the canonical
  directory for the run or project, and that grant covers read/create/edit
  operations only. Every file mutation uses Host-captured preimage checks,
  atomic same-volume replacement where supported, before/after hashes, bounded
  diff summary, run attribution, and recoverable delete/one-time restore
  semantics. Terminal calls run as the current Windows user and can execute
  host code or open sockets; `network_intent` is audit/approval metadata, not
  network isolation. The managed Git commit workflow (Phase 3 Task 6) is not
  shipped; Git commands are ordinary terminal requests.
  `system.volume` is a deterministic direct-action path with same-endpoint
  readback verification, not a model-selection binding. `notes.create`,
  read-only `system.*`, and `app.list` remain Broker-internal. CLI exposes
  `agent approve <run-id> <approval-id>`, `agent deny`, `agent policy
  list`/`import --global FILE`/`remove`, and `agent run --workspace PATH
  "prompt"`. There is no `--scope` flag and no `--preset` flag in the
  normal flow: the Host picks the right one-time token from the pending
  binding — a session-scoped directory grant for
  `external_directory_required`, an ephemeral terminal token for
  `terminal_delete_requires_approval` or `terminal_unknown_executable`.
  `--json` cannot confirm any approval. WinUI displays typed activity
  from actual Host events, and a success is visible only after a matching
  verified Broker receipt.
  The authenticated `POST /agent/v1/runs/{runId}/commands` route and CLI
  `agent command` adapter share one bounded parser for `/compact
  normal|aggressive`, `/help`, `/skills`, and `/skill <id> <json-object>`;
  malformed or unknown slash commands never enter history. Declarative Skills
  require explicit session grants and re-enter normal Broker policy/approval
  checks. The managed Agent enables `web.search` and `web.fetch` by default
  through the fixed DuckDuckGo HTML endpoint
  `https://html.duckduckgo.com/html/`; they are runtime capabilities, not a
  settings/API switch. `files.search` searches the trusted workspace, while
  `web.search` searches the public web. Web responses are bounded,
  redirect-safe, sanitized, and marked `untrusted`; fetched text retains the
  `[untrusted_web_reference]` marker. Connectors and `app.open` remain
  internal.
- Screen observation remains opt-in from Settings. The managed Worker exposes
  bounded observation status/control and the Gemini analysis adapter, but its
  default control path is explicitly disabled until a managed screen-capture /
  reaction loop and trusted credential channel are shipped. The legacy observer
  is not reachable from the managed startup. When events are available, WinUI
  consumes them through the normal voice, subtitle,
  and VTS playback pipeline; the worker never plays observation audio directly.

See [runtime behavior](docs/operations/runtime-behavior.md) for invariants and
manual acceptance boundaries.
See [Agent tools and truthful execution](docs/operations/agent-tools.md) for
the exact current capability matrix.
See the [Phase 3 Python runtime retirement plan](docs/superpowers/plans/2026-08-30-python-runtime-phase-3-retirement.md)
for the completed retirement record and its remaining native acceptance boundary.

## Verification

Run heavyweight gates sequentially. The deterministic backend Harness owns a
temporary test-fixture worker/Broker/edge session with dynamic loopback ports
and emits a safe schema-v2 artifact; it does not launch a production Python
worker:

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

The backend artifact covers 64 HTTP routes and 1 WebSocket route (`/voice/stream`; 43 non-Agent
plus 20 `/agent/v1/*` plus 1 `POST /host/shutdown`), requires correct-bearer success plus
wrong/missing-bearer rejection, and records observation routes as explicit
hardware/external skips. It uses synthetic STT/TTS and records only statuses,
timings, counts, event types, and cleanup state. In deterministic mode it also
exercises one read-only `files.read` Agent tool loop. Broker approval/replay is
checked separately with a temporary `notes.create` fixture. At this
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
dotnet publish D:\LLM\gui-winui3\RuApp.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:SelfContained=true --nologo
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

The retired `runtime-live` command is a bounded diagnostic; it reports
`python_compatibility_retired` without starting any child topology. A
managed-native live voice Harness is outside this retirement phase. Native
audio/VTube acceptance remains user-owned:

```powershell
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode runtime-live
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

Visible assistant text may cross the private generation boundary. Credentials,
audio bytes, VTS actions, voice timing/control metadata, and transcripts must
not enter logs, cleanup manifests, memory, history, or reviewed examples.

## Project boundaries

Model weights, voice assets, active datasets, credentials, runtime app data,
and local caches are not source files. Do not commit them. Do not treat a model
download, unit-test pass, benchmark score, or historical report as live quality
acceptance.

