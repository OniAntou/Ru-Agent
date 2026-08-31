# Verification

Run heavyweight gates sequentially on Windows. Restore each .NET test project
before using `--no-restore`; concurrent Python, frontend, and .NET builds can
exhaust the page file on this workspace.

## Deterministic release matrix

```powershell
dotnet restore D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj --nologo
& D:\LLM\.venv\Scripts\python.exe -m ruff check D:\LLM\src D:\LLM\tests D:\LLM\scripts D:\LLM\tools
& D:\LLM\.venv\Scripts\python.exe -m mypy D:\LLM\src
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\measure_phase1_runtime.py --root D:\LLM
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output "$env:TEMP\ru-prompt-composer-harness.json"
& D:\LLM\.venv\Scripts\python.exe -m pip check
& D:\LLM\scripts\test_dotnet.ps1 -Configuration Release
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --no-restore --nologo
cmd /c D:\LLM\build.bat
```

CI and local verification share `scripts/test_dotnet.ps1`, which recursively
discovers `*Tests.csproj` under `backend-dotnet` and `gui-winui3.Tests`, excluding
generated `bin`, `obj`, and `out` paths. It runs them sequentially and stops on
missing roots, discovery errors, an unavailable SDK, or the first test failure.
It restores by default; use `-NoRestore` only after restoring the projects.
Protocol and all three Runtime test projects are included.

The test cleanup removed the unused Python VTS/overlay package and its tests,
including the test that could open a real VTube Studio connection. Managed C#
VTS tests and opt-in native acceptance remain separate. Build and launcher
unit tests replace only their external process boundary and never open RuApp.

### Test-suite cleanup evidence (2026-08-31)

- Python: `402 passed` (audit baseline: `510`); unused VTS/overlay tests,
  synthetic file-tool simulations, and redundant guards were removed or
  consolidated, with new build/launcher/runner behavior checks added.
- .NET Release: `2,009 passed` across all 13 projects, including backend
  `633` and GUI `252`. The GUI reduction from `339` removes cosmetic snapshots,
  retaining three focused resource/accessibility contracts and behavior tests.
- Ruff, Mypy (`39` source files), `pip check`, and the
  `56 HTTP + 1 WebSocket` public route contract all passed.
- The first .NET run encountered a business-fixture JSONL cleanup lock. Only
  those business tests now inject a discarding diagnostic emitter; the `89`
  focused business/diagnostics tests and subsequent full matrix passed.
  The real writer's bounded shutdown wait still needs investigation, tracked
  in [TODO](../../TODO.md). This cleanup does not claim that lifecycle bug fixed.
- Cleanup verification did not publish a fresh GUI, automate the interactive
  GUI, perform real voice/VTS/cloud acceptance, or uninstall packages.
  Commit/push is a separately authorized Git handoff, not runtime acceptance.

The route checker must remain at 56 HTTP routes and 1 WebSocket
route (`/voice/stream`; 41 legacy plus 15 additive `/agent/v1/*`). The
deterministic Harness is contract evidence. A successful build proves only compilation,
publish output, and the incremental native overlay build/test invocation; it
does not prove that a GUI, microphone, speaker, VTube Studio model, or Windows
audio endpoint worked.

The managed runtime topology is deterministic: one C# Worker serves the Agent
loopback endpoint (`/worker/v2`) and the separate device endpoint
(`/worker/v1`). Worker tokens are distinct; provider credentials are carried
only by the single-use DPAPI bootstrap and never by the device graph,
legacy Python configuration, descriptors, logs, or public responses. The
production Python generation/worker modules and compatibility launch branch
are retired; the test-only fixture under `scripts/harness` is not a production
fallback. The retired `runtime-live` command is a bounded diagnostic and does
not start a child topology.

The Phase 1 measurement command times deterministic prompt/tool operations in
small batches to amortize sub-millisecond scheduler noise, then measures their
allocation peak in a separate `tracemalloc` pass. It compares p50, p95, and
peak bytes with the checked-in baseline using the ten-percent gate plus a
bounded 0.05 ms timing-jitter floor; it never loads a model or contacts a
device.

The Worker v2 model inventory is deterministic and should stay aligned with
`AgentRuntimeCoordinatorFactory.RuntimeCapabilities`: `files.list`,
`files.read`, `files.glob`, `files.search`, `files.write`, `files.edit`,
`files.delete`, `files.restore`, `terminal.run`, and `terminal.shell`. Standard
and Full Access receive the same advertised list; policy fallback behavior is
verified separately. `system.volume` is a direct deterministic action and is
not a model-selection binding. `notes.create`, read-only `system.*`, and
`app.list` remain Broker-internal. `files.apply_patch` remains unadvertised.
Typed commands (`/compact`, `/help`, `/skills`, `/skill`) use the shared
Protocol parser and are covered by CLI/Host tests. Web search/fetch are
enabled read-only managed capabilities backed by the fixed DuckDuckGo HTML
endpoint; each call remains bounded, redirect-safe, policy-checked, and marked
untrusted. Declarative Skills are explicit session grants with per-step policy
checks. Connectors and `app.open` remain unadvertised.

### Session deletion regression evidence (2026-08-31)

The GUI client regression test uses an isolated loopback HTTP listener that
returns HTTP 200 with `status: "error"`. `ApiService.DeleteSessionAsync` must
reject that response, and the WinUI delete handler must not refresh or create a
replacement session after application-level failure:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --filter "FullyQualifiedName~SessionDeletionApiTests"
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --filter "FullyQualifiedName~Session"
```

The pre-cleanup verification passed the focused regression (`1/1`), the then
full GUI suite (`339/339`), and the backend session slice (`39/39`). These
behavior tests remain; the cleanup only removed cosmetic GUI checks. No
production session or user data is used by the regression listener.

The desktop launcher and release build share one GUI artifact, and the release
build publishes the managed Worker before the backend:
`gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe`.
`backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Worker.exe`.
`backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Core.dll`.
`build.bat` fails if any of these managed artifacts is absent and verifies that
the GUI runtimeconfig contains `includedFrameworks`, proving the publish is
self-contained. `start_dev.bat` refuses to run when the Worker Release artifact
is missing instead of silently selecting a stale Debug executable; it passes the
workspace directory to a short startup probe so an immediate `RuApp.exe`
failure remains visible. A clean exit code from a second launch is accepted when
the existing single-instance GUI remains alive. Any Windows shortcut should
target `start_dev.bat` so it uses the same release-path and startup checks.

Phase 2 parity is deterministic and status-only. The shared fixture
`tests/fixtures/python-csharp-phase2-device-baseline.json` is exercised by
`RuAgent.Runtime.Worker.Tests/Phase2ManagedDeviceParityTests.cs`; these tests
cover v1 types/sequences, settings shape, voice control ordering, stable
errors, STT normalization, VTS code sets, observation bounds, cancellation,
and forbidden-value redaction without starting a provider or device.

Ruff, Mypy, and `pip check` are required dependency/tooling gates; do not hide
findings with permanent ignores or `continue-on-error`. Live microphone,
speaker, volume, and VTube Studio acceptance remain explicit user-run checks.

### Phase 2 entry evidence before Python retirement (2026-08-30)

This is the deterministic evidence retained from the checkout before the Phase 3
retirement changes:

| Gate | Result |
| --- | --- |
| Python reference/tooling suite | `501 passed` |
| C# Release matrix | `2,085 passed` across 13 test assemblies (14 restored projects including the Harness build) |
| Route contract | `56 HTTP + 1 WebSocket` |
| Runtime-boundary scan | `865` files; schema 1 reported `reachable_count=2` (a startup-path label, not graph analysis) |
| Backend deterministic Harness | `contract` / `passed`; owned cleanup green |
| Agent GUI Harness | 4/4 supported Agent scenarios passed |
| Release build | `build.bat` `13/13`; GUI and managed Worker published |

Managed startup now rejects an explicit legacy `PythonCompatibility` setting
before acquiring a mutex, writing a descriptor, or starting child processes;
the stable code is `python_compatibility_retired`.

The logging benchmark remains an optional latency probe. Its percentile tail
is scheduler-sensitive on this Windows host; a noisy run is recorded as an
environmental measurement failure and does not change the deterministic
runtime gate. The per-writer enqueue tests remain the blocking logging guard.

### Phase 2 manual acceptance record (2026-08-30)

The deterministic release gates are automated and do not stand in for native
device behavior. The following checks are intentionally pending a user-owned
Windows run. Automated verification may launch isolated RuApp, Worker, and
Broker processes, but it does not touch the physical microphone, speaker,
volume endpoint, VTube Studio, or provider process:

| Check | Status | Recorded evidence |
| --- | --- | --- |
| Microphone capture and no-speech handling | pending user-run | not collected |
| OmniVoice stream, cancellation, playback, and subtitle order | pending user-run | not collected |
| VTube Studio catalog, action, and reconnect | pending user-run | not collected |
| Opt-in Gemini observation | pending user-run | not collected |

The authorized 2026-08-30 run passed all four deterministic Agent GUI
scenarios and the backend contract Harness. The retired `runtime-live` command
returns `startup.readiness=blocked_by_startup` with reason
`python_compatibility_retired` and points to `managed_runtime_native`; it does
not start a child topology. No native microphone, audio, VTube Studio, or
Gemini quality claim is made here.

When these checks are run, record only pass/fail, duration, and bounded reason
codes. Never copy prompts, transcripts, audio, credentials, paths, provider
responses, or device values into this document or its artifacts.

### Historical Phase 3 retirement evidence (2026-08-30)

The retirement changes were verified sequentially before the test-suite cleanup:

| Gate | Result |
| --- | --- |
| Python suite | `504 passed` |
| Ruff | passed |
| Mypy | passed (`50` source files) |
| Dependency check | `pip check` passed |
| Python compile check | `compileall` passed |
| C# Release matrix | `2,086 passed` across 13 test assemblies |
| Route contract | `56 HTTP + 1 WebSocket` |
| Runtime-boundary scan | `864` files; schema 1 reported `reachable_count=2` (a startup-path label, not graph analysis) |
| Backend deterministic Harness | `48/56` HTTP passed, `8` external skipped, `1/1` WebSocket passed; cleanup green |
| Agent GUI Harness | 4/4 supported Agent scenarios passed |
| Managed startup smoke | `/status` `200`, wrong bearer `401`; Worker and Broker ports listened and shut down cleanly |
| Retired selector smoke | exit `5`; `python_compatibility_retired`; no child topology |
| Release build | `build.bat` `13/13`; GUI, managed Worker/Core, Broker, and backend published |
| `git diff --check` | passed |

The `runtime-live` command is intentionally retained only as a bounded
retirement diagnostic; it returns `startup.readiness=blocked_by_startup` with
`python_compatibility_retired` and does not launch any child. The GUI voice
runner and native microphone, speaker, OmniVoice, VTube Studio, and Gemini
checks remain separate acceptance boundaries; the Agent GUI scenarios above
exercise the deterministic Agent path only.

## Prompt composer contract checks

Run these focused checks when changing prompt context, composition, Worker v2,
or receipt persistence. Run them sequentially. They cover the canonical seven
layers, strict context/receipt validation, root-only instruction loading and
final-path escape handling, the C# wire codec, internal persistence, and public
SSE cursor suppression.

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_python_runtime_boundary.py
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AgentPromptReceipt|FullyQualifiedName~SystemPromptContract"
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RuInstructionLoader|FullyQualifiedName~RuPromptContextProvider|FullyQualifiedName~WorkerV2Prompt|FullyQualifiedName~WorkerV2Contract|FullyQualifiedName~WorkerV2Client|FullyQualifiedName~AgentSseReconnect"
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~HarnessPayloadStore|FullyQualifiedName~HarnessProjectionTransaction"
```

The deterministic Harness artifact is status-only contract evidence. It and all public
events must exclude prompt text, receipts, raw instruction contents, tool
arguments, filesystem paths, and provider secrets. `model.prompt_composed` is a
durable internal event only; it must remain absent from the public event
allowlist and from CLI/WinUI rendering. Deterministic checks do not replace
live microphone, speaker, VTube Studio, or real Windows endpoint acceptance.

The backend-deterministic Harness is a separate synthetic fixture topology. It
starts the dependency-free test-only `harness.deterministic_worker`, which
serves the v1/v2 contract probes without importing production
`ru_runtime.generation` or `ru_runtime.worker` modules. This keeps contract
probes isolated from the published managed Worker; normal desktop startup is
managed C# only.

This fixture evidence does not prove raw Worker v1 route parity in the
default managed mode. The managed composition now binds every allowlisted
private route to an explicit C# handler. Status/models expose only the active
typed settings (provider credentials remain host-owned), runtime-scoped VTS
state/config and canonical token persistence, and
catalog/action/connect use the managed adapter, voice/STT use typed providers,
and observation control is explicitly disabled until its managed capture and
reaction loop exists. Overlay enable is fail-closed while its process owner is
missing. The generic private-route fallback is no longer reachable
from the managed composition; provider-backed model catalogs, VTS overlay
process/semantic actions, and observation capture/reaction still require a
dedicated managed-runtime integration gate before claiming full production
parity.

On 2026-08-30, a bounded smoke of the freshly published managed runtime used
isolated temporary roots and loopback ports: the authenticated Public Host
`/settings` and Broker `/health` checks returned `200`, while the Agent
`/worker/v2` and Device `/worker/v1` listeners returned the expected `400` for
non-WebSocket probes. The owned process tree was stopped and the probe roots
were removed. This confirms managed startup/listener wiring only; it does not
replace native microphone, OmniVoice, VTube Studio, or Gemini acceptance.

## Test ownership and documentation integrity

Python tests cover the retained OmniVoice provider, reference fixtures and
maintenance/harness tooling. Managed C# suites cover the Public Host, Broker,
Worker, settings migration, session deletion, privacy and cancellation.
Do not keep a copied implementation in a test as evidence about production.

Documentation checks validate local links and ADR metadata, not exact wording,
ADR counts, colors or historical deletion lists. GUI structural checks are
not visual acceptance; keep real responsive-policy and interaction tests.

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_current_documentation.py
```

## evidence_class: contract

Contract evidence proves protocol shape, lifecycle ordering, authorization,
privacy, and cleanup without performing a valid native mutation.

```powershell
$artifact = Join-Path $env:TEMP 'ru-action-kernel-contract.json'
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output $artifact
$report = Get-Content -Raw -LiteralPath $artifact | ConvertFrom-Json
if ($report.evidence_class -ne 'contract') { throw 'wrong evidence class' }
if ($report.summary.status -ne 'passed') { throw 'contract Harness failed' }
```

The Harness owns temporary Worker, Broker, Public Host, credentials, loopback
ports, data root, and workspace. Its synthetic Worker exercises a read-only
`files.read` Agent step against the temporary fixture. The canonical binding
must emit one bounded tool activity before the Agent completion and must not
open a native device endpoint. The artifact contains statuses and safe reason
codes only; raw tool arguments, receipts, prompts, and replies remain absent.

A separate authenticated private-Broker probe opens only `workspace.write`,
requests `notes.create` in the temporary workspace, approves once, expects the
replay to be denied as `approval_stale`, closes the session, and removes the
fixture. The schema-v2 artifact contains bounded statuses and cleanup
booleans, not request arguments, note content, workspace paths, tokens, or
native output values.

For a cold start, the Harness keeps all three port leases while it builds the
Broker and Public Host projects sequentially, then launches each with
`--no-build`. This avoids concurrent writes to their shared
`RuAgent.Protocol` intermediate output. If a child still exits before
readiness, `startup.readiness` reports `child_unexpected_exit` plus only the
child role, exit code, and lifecycle classification; a live child that never
becomes ready reports `readiness_timeout`.

## evidence_class: adapter

Adapter evidence proves result semantics with fake endpoints, temporary files,
and bounded child processes. It does not open the real Windows audio endpoint.

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_harness_artifacts.py tests/test_harness_session.py tests/test_agent_harness.py tests/test_native_volume_acceptance.py
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AdapterConformanceTests|FullyQualifiedName~VolumeToolsTests|FullyQualifiedName~TerminalAdapterTests|FullyQualifiedName~ProcessTreeRunnerTests"
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentApprovalTests|FullyQualifiedName~AgentApprovalUiContractTests"
```

These tests establish file write/readback and cleanup, notes approval,
terminal zero/non-zero/timeout/start-failure outer statuses, volume argument
validation before endpoint acquisition, verified fake-endpoint success, and
readback-mismatch failure. They also establish restoration logic and artifact
privacy for the opt-in native runner through injected process, HTTP,
WebSocket, and artifact-writer seams.

Host tests cover cross-session rotation with a stateful single-owner fake and
prove close-before-open, same-session reuse, and no dispatch after close failure.

External-directory tests prove canonical descendant grants for read/list and
create/edit, sibling isolation, absolute-path output, project-scope reuse across
runs, and delete/root-directory approval gates.

## evidence_class: native

Native evidence is an explicit user-owned smoke against a freshly published
`RuAgent.Broker.exe`. It is not part of CI, the default build, or the
deterministic Harness. Do not run it without the confirmation flag:

```powershell
$broker = 'D:\LLM\backend-dotnet\RuAgent.Broker\bin\Release\net10.0\win-x64\publish\RuAgent.Broker.exe'
$artifact = Join-Path $env:TEMP 'ru-native-volume.json'
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\native_volume_acceptance.py --broker-exe $broker --output $artifact --confirm-native-volume-change
```

The runner proves the correct bearer and a fresh wrong bearer before opening
the private WebSocket, captures the original state, changes volume by one
point, verifies the same endpoint role/hash, and restores level and mute state
in `finally`. Restoration failure forces exit 1 and a failed native artifact.
Both CLI paths must be absolute, the output must be a `.json` file distinct
from the published Broker, and the child Broker receives only the required
descriptor/workspace variables plus a small Windows process environment
allowlist; unrelated Ru and provider credentials are not inherited.
The artifact excludes levels, mute values, credentials, paths, arguments,
outputs, device names, and native exception text.

The authorized native volume acceptance on 2026-08-30 passed both the one-point
change/readback and the restoration check, with all owned Broker artifacts and
processes cleaned up. The runner opens the Broker with the `agent` capability
profile so `system.volume` is visible after the Agent-only cutover; it does not
record the endpoint level, mute state, device identity, or request values.

Even a passing native artifact does not prove audible quality or that the user
observed the GUI behave correctly. The user must separately inspect the
Windows playback level and confirm the restored state.

## Deterministic GUI Harness

The GUI Harness is manual and interactive because it launches WinUI. It uses
synthetic microphone/speaker boundaries and refuses to start while another
RuApp instance exists. The supported Agent scenarios are exactly:

- `agent-volume-contract`
- `agent-cancel`
- `agent-stale-result`
- `agent-private-metadata`

The cancel and stale-result scenarios replace the Agent session while its
synthetic tool request is still in flight, wait beyond the delayed result, and
fail if a user/assistant message, private marker, or enabled Agent mode leaks
back into the replacement session.

Each Agent GUI run receives a fresh temporary workspace through the Harness
descriptor, creates the bounded `harness-agent.txt` fixture, and approves the
workspace-trust card through UI Automation before the model step starts. The
`agent-volume-contract` identifier is retained for catalog compatibility; its
Agent-only fixture now exercises the read-only `files.read` loop and checks the
bounded terminal reply. It never opens or changes the Windows volume endpoint.

HTTP route probes use schema-valid VTS fixtures: the complete `MotionConfig`
shape for `/vts/config`, a `typing` boolean for `/vts/activity`, and a bounded
codes string for `/vts/action`. This keeps compatibility probes aligned with
the typed route contract; live VTube Studio reconnect/action behavior remains
a separate user-run acceptance gate.

The UI driver verifies the static Agent indicator without requiring an idle status row;
`AgentStatusText` is expected only after an Agent request or another activity
that needs user interaction. Typed requested, granted, verified, and failed
copy is covered by WinUI contract tests. Agent artifacts and event-sink records
contain only bounded states, counts, tool IDs, durations, and safe reason
codes—not arguments, output, levels, amounts, mute state, prompts, replies,
device identity, endpoint hashes, or exception text. The WinUI activity feed
additionally accepts only typed transient `agent.activity` fields:
basename/line range, symbol, counts, edit counts, duration, and `safe_code`;
malformed or content-bearing transient frames are ignored and never become
durable history.

Broker approval dispatch/replay is covered by the private deterministic probe;
the visible approval card remains covered by `AgentApprovalTests` and
`AgentApprovalUiContractTests`. GUI Harness scenarios do not claim that
terminal, files, Web, connectors, or skills are model-usable.

Agent-only visual acceptance covers the GUI cleanup in Task 7 of the approved
plan. The composer leaves one static Agent indicator and one compact preset
selector (`Standard`/`Full Access`), keeps the welcome layout fixed, and uses
the Agent HTTP/SSE path for every prompt. Grow a transcript until scrolling is
needed and verify messages stay centered while the native vertical scrollbar
sits at the far-right edge; horizontal scrolling must remain disabled. These
remain manual release checks; deterministic tests cover the source and layout
contract but do not replace visual inspection.

A passing deterministic GUI result does not claim an audible Windows volume
change and does not accept live microphone capture, audible OmniVoice,
subtitles, or VTube Studio. The legacy runtime-live compatibility probe is
blocked until a managed-native live path is available; physical microphone
accuracy, speaker quality, natural bilingual conversation, and VTube Studio
behavior remain explicit user-run checks. Real volume mutation remains the
opt-in `native_volume_acceptance.py` path with
`--confirm-native-volume-change` and separate user confirmation.

See [Agent tools and truthful execution](agent-tools.md) for the current
capability matrix and ownership boundary.
