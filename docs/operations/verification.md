# Verification

Run heavyweight gates sequentially on Windows. Restore each .NET test project
before using `--no-restore`; concurrent Python, frontend, and .NET builds can
exhaust the page file on this workspace.

## Deterministic release matrix

```powershell
dotnet restore D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo
dotnet restore D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --nologo
dotnet restore D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj --nologo
dotnet restore D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj --nologo
dotnet restore D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj --nologo
dotnet restore D:\LLM\backend-dotnet\RuAgent.Hosting.Tests\RuAgent.Hosting.Tests.csproj --nologo
dotnet restore D:\LLM\backend-dotnet\RuAgent.Client.Tests\RuAgent.Client.Tests.csproj --nologo
dotnet restore D:\LLM\backend-dotnet\RuAgent.Cli.Tests\RuAgent.Cli.Tests.csproj --nologo
dotnet restore D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
dotnet restore D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj --nologo
& D:\LLM\.venv\Scripts\python.exe -m ruff check D:\LLM\src D:\LLM\tests D:\LLM\scripts D:\LLM\tools
& D:\LLM\.venv\Scripts\python.exe -m mypy D:\LLM\src
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\aspnetcore_compatibility_harness.py --mode backend-deterministic --output "$env:TEMP\ru-prompt-composer-harness.json"
& D:\LLM\.venv\Scripts\python.exe -m pip check
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Connectors.Tests\RuAgent.Connectors.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Hosting.Tests\RuAgent.Hosting.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Client.Tests\RuAgent.Client.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Cli.Tests\RuAgent.Cli.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --nologo
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Release --no-restore --nologo
cmd /c D:\LLM\build.bat
```

This is the local equivalent of the CI release gates and must run
sequentially. The route checker must remain at 54 HTTP routes and 1 WebSocket
route (`/voice/stream`; 41 legacy plus 13 additive `/agent/v1/*`). The compatibility Harness is
deterministic contract evidence. A successful build proves only compilation,
publish output, and the incremental native overlay build/test invocation; it
does not prove that a GUI, microphone, speaker, VTube Studio model, or Windows
audio endpoint worked.

The desktop launcher and release build share one artifact:
`gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe`.
`build.bat` fails if the GUI publish is absent, and `start_dev.bat` refuses to
run when that Release artifact is missing instead of silently selecting a stale
Debug executable. Any Windows shortcut should target `start_dev.bat` so it uses
the same release-path check.

Ruff, Mypy, and `pip check` are required dependency/tooling gates; do not hide
findings with permanent ignores or `continue-on-error`. Live microphone,
speaker, volume, and VTube Studio acceptance remain explicit user-run checks.

## Prompt composer contract checks

Run these focused checks when changing prompt context, composition, Worker v2,
or receipt persistence. Run them sequentially. They cover the canonical seven
layers, strict context/receipt validation, root-only instruction loading and
final-path escape handling, the C# wire codec, internal persistence, and public
SSE cursor suppression.

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q `
  D:\LLM\tests\test_system_prompt_composer.py `
  D:\LLM\tests\test_worker_v2_protocol.py `
  D:\LLM\tests\test_worker_v2_step.py `
  D:\LLM\tests\test_worker_v2_server.py
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AgentPromptReceipt|FullyQualifiedName~SystemPromptContract"
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RuInstructionLoader|FullyQualifiedName~RuPromptContextProvider|FullyQualifiedName~WorkerV2Prompt|FullyQualifiedName~WorkerV2Contract|FullyQualifiedName~WorkerV2Client|FullyQualifiedName~AgentSseReconnect"
dotnet test D:\LLM\backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~HarnessPayloadStore|FullyQualifiedName~HarnessProjectionTransaction"
```

The compatibility artifact is status-only contract evidence. It and all public
events must exclude prompt text, receipts, raw instruction contents, tool
arguments, filesystem paths, and provider secrets. `model.prompt_composed` is a
durable internal event only; it must remain absent from the public event
allowlist and from CLI/WinUI rendering. Deterministic checks do not replace
live microphone, speaker, VTube Studio, or real Windows endpoint acceptance.

## Documentation contract check

The check below prevents active documents from claiming that the shipped
composer is pending, and verifies that Worker v2 has no top-level
`memory_context` field while the internal receipt event stays out of the public
allowlist:

```powershell
$activeDocs = @(
    'D:\LLM\docs\README.md',
    'D:\LLM\docs\architecture.md',
    'D:\LLM\docs\operations\runtime-behavior.md',
    'D:\LLM\docs\operations\verification.md'
)
$stalePattern = @(
    ('implementation not ' + 'started'),
    ('not ' + 'implemented')
)
$stale = Select-String -Path $activeDocs -Pattern $stalePattern
if ($stale) { throw 'stale composer status remains in active docs' }
$workerPaths = @(
    'D:\LLM\src\ru_runtime\worker\v2',
    'D:\LLM\backend-dotnet\RuBackend\Harness\Worker',
    'D:\LLM\backend-dotnet\RuAgent.Protocol\Harness'
)
$legacyMemory = & rg -n ('"memory' + '_context"') $workerPaths
if ($LASTEXITCODE -eq 0) { throw 'top-level Worker v2 memory field remains' }
if (Select-String -Path 'D:\LLM\backend-dotnet\RuAgent.Protocol\Harness\HarnessEventContracts.cs' -Pattern ('model' + '\.prompt_composed')) {
    throw 'internal prompt event is documented as public'
}
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
subtitles, or VTube Studio. The separate runtime-live voice gate can require
emitted audio, but physical microphone accuracy, speaker quality, natural
bilingual conversation, and VTube Studio behavior remain explicit user-run
checks. Real volume mutation remains the opt-in `native_volume_acceptance.py`
path with `--confirm-native-volume-change` and separate user confirmation.

See [Agent tools and truthful execution](agent-tools.md) for the current
capability matrix and ownership boundary.
