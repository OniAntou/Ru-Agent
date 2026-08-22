# Ru Volume Tool Vertical Slice Implementation Plan

Status: Superseded; do not execute. See the approved [Ru Action Execution Kernel v1 design](../specs/2026-08-22-ru-action-execution-kernel-v1-design.md) and its [implementation plan](2026-08-22-ru-action-execution-kernel-v1.md).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Ru's Windows volume capability truthful and usable in normal chat and Agent mode through one typed, readback-verified contract.

**Architecture:** Add a focused Python volume domain module for request validation, legacy marker normalization, and the normal-chat adapter. Add a focused C# Broker volume tool with the same operation/result contract and an injectable Windows Core Audio implementation. The Worker/Host path will distinguish initial structured tool generation from the natural-language continuation after a tool result.

**Tech Stack:** Python 3.11–3.14, pytest, pycaw/comtypes for Python Core Audio access, ASP.NET Core/.NET 10, C# COM interop for the Broker adapter, xUnit, existing Worker/Broker WebSocket contracts, and the deterministic Harness.

## Global Constraints

- Preserve all unrelated dirty files; only stage files listed by the current task.
- Keep `system.volume@1.0.0` bounded to the Windows default render endpoint.
- Accept `set.level` only in the inclusive range 0–100; accept relative `amount` only in the inclusive range 1–50.
- Normalize legacy `up/down` markers to a four-percentage-point relative change.
- Do not use PowerShell, shell execution, simulated keypresses, admin elevation, or a real audio device in automated tests.
- A write is successful only after Core Audio readback confirms the requested level or mute state.
- Do not put raw device names, native errors, credentials, arguments, or transcripts into chat, TTS, memory, or safe activity summaries.
- Run heavyweight Python and .NET suites sequentially on Windows.
- The manual acceptance gate remains separate from deterministic test success.

---

## File Map

| File | Responsibility |
| --- | --- |
| `src/ru_runtime/system/volume.py` | Canonical Python request/result types, validation, legacy normalization, fakeable adapter boundary, and Windows Core Audio adapter. |
| `src/ru_runtime/system/pc_tools.py` | Preserve the existing `adjust_system_volume` compatibility function by delegating to the canonical volume module. |
| `src/ru_runtime/generation/orchestration.py` | Parse volume markers, execute the canonical request, remove internal metadata, and replace failed-action prose with a truthful response. |
| `src/ru_runtime/policy/style.py` | Separate normal marker instructions from structured Agent tool instructions and the tool-result follow-up instruction. |
| `src/ru_runtime/generation/runner.py` | Select normal, initial Agent-tool, or Agent-follow-up system instructions and marker policy. |
| `src/ru_runtime/worker/v1/handlers.py` | Mark generation phase, emit structured requests, and prevent failed volume results from reaching a success-claiming continuation. |
| `backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs` | Validate and execute the Broker-facing `system.volume` contract through an injectable controller. |
| `backend-dotnet/RuAgent.Broker/Tools/Desktop/WindowsCoreAudioVolumeController.cs` | Native Windows default-render Core Audio implementation used by the Broker. |
| `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs` | Register `system.volume@1.0.0` with `system.write`, reversible risk, and explicit schemas. |
| `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs` | Dispatch `system.volume` and accept an injectable controller for tests. |
| `backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs` | Thread the optional test controller into the Broker tool service without changing production startup behavior. |
| `backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs` | Grant `system.write` and map `system.volume` to that scope. |
| `tests/test_volume_control.py` | Python contract, fake-adapter, validation, and readback tests. |
| `tests/test_system_pc_tools.py` | Compatibility wrapper tests. |
| `tests/test_reply_orchestration.py` | Normal marker success/failure rendering tests. |
| `tests/test_runner_status.py` | Runner phase and marker-policy tests. |
| `tests/test_worker_runtime_handlers.py` | Agent structured request, result continuation, and failure fallback tests. |
| `backend-dotnet/RuAgent.Broker.Tests/VolumeToolsTests.cs` | Broker tool validation and fake-controller tests. |
| `backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs` | Non-destructive catalog smoke matrix. |
| `backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs` | Authenticated fake-volume WebSocket path. |
| `backend-dotnet/RuBackend.Tests/Conversation/AgentTurnCoordinatorTests.cs` | Host scope mapping and session grant coverage. |
| `pyproject.toml` | Add the Python Core Audio runtime dependencies. |
| `docs/operations/agent-tools.md` | Record the shipped Agent volume tool and reversible scope. |
| `docs/operations/verification.md` | Record deterministic versus manual volume acceptance. |

---

### Task 1: Build the canonical Python volume contract

**Files:**
- Create: `D:\LLM\src\ru_runtime\system\volume.py`
- Modify: `D:\LLM\pyproject.toml`
- Create: `D:\LLM\tests\test_volume_control.py`

**Interfaces:**
- Produces `VolumeRequest`, `VolumeState`, `VolumeResult`, `VolumeController`, `parse_volume_action`, and `execute_volume_request` for subsequent Python tasks.
- `VolumeController.get_state() -> VolumeState`, `set_level(level: int) -> None`, and `set_muted(muted: bool) -> None` are the only adapter operations.
- `execute_volume_request(request: VolumeRequest, controller: VolumeController | None = None) -> VolumeResult` uses the production Windows adapter when `controller` is omitted and a fake adapter in tests.

- [ ] **Step 1: Write failing contract tests**

Add tests that assert these exact behaviors:

```python
def test_parse_volume_action_supports_exact_level() -> None:
    assert parse_volume_action("set 2") == VolumeRequest("set", level=2)


def test_parse_volume_action_keeps_legacy_up_down_compatibility() -> None:
    assert parse_volume_action("up") == VolumeRequest("increase", amount=4)
    assert parse_volume_action("down") == VolumeRequest("decrease", amount=4)


def test_set_request_reads_back_the_verified_level() -> None:
    controller = FakeVolumeController(level=64, muted=False)
    result = execute_volume_request(VolumeRequest("set", level=2), controller)
    assert result.ok is True
    assert result.state == VolumeState(level=2, muted=False)
    assert result.verified is True


def test_readback_mismatch_is_a_failure() -> None:
    controller = FakeVolumeController(level=64, muted=False, ignore_writes=True)
    result = execute_volume_request(VolumeRequest("set", level=2), controller)
    assert result.ok is False
    assert result.error_code == "volume_readback_mismatch"
    assert result.verified is False
```

Also add parameterized validation tests for missing `level`, missing/invalid
`amount`, values outside the allowed ranges, unknown operations, and `mute`/
`unmute` readback.

- [ ] **Step 2: Run the focused tests and verify the expected red failure**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_volume_control.py
```

Expected result: collection or assertion failures because `volume.py` and its
public contract do not yet exist. Do not change production code until this
failure is observed.

- [ ] **Step 3: Add the minimal domain and adapter implementation**

Implement in `volume.py`:

```python
@dataclass(frozen=True, slots=True)
class VolumeRequest:
    operation: Literal["get", "set", "increase", "decrease", "mute", "unmute"]
    level: int | None = None
    amount: int | None = None


@dataclass(frozen=True, slots=True)
class VolumeState:
    level: int
    muted: bool


@dataclass(frozen=True, slots=True)
class VolumeResult:
    ok: bool
    state: VolumeState | None
    verified: bool
    error_code: str | None
    message: str
```

`parse_volume_action` accepts `get`, `set N`, `increase N`, `decrease N`,
`mute`, `unmute`, and maps `up`/`down` to four percentage points. It raises a
bounded `VolumeValidationError` for malformed values. `execute_volume_request`
validates first, reads the current state, applies the operation, reads again,
and marks a write successful only when the requested state matches.

Use lazy imports for `pycaw` and `comtypes` so pure parser tests do not touch
COM. Add `pycaw` and `comtypes` to the project dependencies. The production
adapter returns `volume_unsupported_platform` outside Windows and maps native
exceptions to stable error codes without exposing exception text.

- [ ] **Step 4: Run the focused tests and verify green**

Run the same pytest command. Expected result: all contract tests pass without
changing the real audio device.

- [ ] **Step 5: Commit only the Python contract files**

```powershell
git add -- pyproject.toml src/ru_runtime/system/volume.py tests/test_volume_control.py
git commit -m "feat: add verified volume contract"
```

---

### Task 2: Route normal-chat markers through the contract

**Files:**
- Modify: `D:\LLM\src\ru_runtime\system\pc_tools.py`
- Modify: `D:\LLM\src\ru_runtime\generation\orchestration.py`
- Modify: `D:\LLM\src\ru_runtime\policy\style.py`
- Modify: `D:\LLM\tests\test_system_pc_tools.py`
- Modify: `D:\LLM\tests\test_reply_orchestration.py`

**Interfaces:**
- Consumes `parse_volume_action` and `execute_volume_request` from Task 1.
- Preserves `adjust_system_volume(action: str, steps: int = 2) -> tuple[bool, str]` for existing callers, mapping `steps` to the legacy four-point default only when the caller uses the old interface.
- `_execute_pc_tool_markers(text: str) -> str` remains the orchestration seam used by `execute_reply`.

- [ ] **Step 1: Write failing normal-chat tests**

Add tests for `[VOLUME: set 2]` and `[VOLUME: get]` using a fake controller
patched at the new adapter boundary. Add a failure test where the fake
controller refuses the write and the returned text does not contain a success
claim such as `đã đặt` or `đã giảm`.

- [ ] **Step 2: Run the focused tests and verify red**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_system_pc_tools.py tests/test_reply_orchestration.py
```

Expected result: the new marker and truthful-failure tests fail because the
current executor only accepts free-form legacy actions and ignores result
status.

- [ ] **Step 3: Replace the old volume marker branch**

In `orchestration.py`, parse each marker payload with `parse_volume_action`,
execute it through `execute_volume_request`, remove the marker, and return a
bounded Vietnamese failure response if the result is not successful. Keep the
existing `[APP_OPEN: ...]` behavior unchanged. Do not log raw marker content.

In `pc_tools.py`, make `adjust_system_volume` a compatibility wrapper around
`VolumeRequest` and `execute_volume_request`; remove all `keybd_event` calls.

In `style.py`, replace the old volume examples with the six canonical marker
forms plus the explicit rule that the model must not claim success when a
control is unavailable. Keep the marker block out of the future Agent prompt.

- [ ] **Step 4: Run Python regression tests**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_system_pc_tools.py tests/test_reply_orchestration.py tests/test_volume_control.py
```

Expected result: all selected tests pass, including the existing app-open and
search-marker behavior.

- [ ] **Step 5: Commit the normal-chat slice**

```powershell
git add -- src/ru_runtime/system/pc_tools.py src/ru_runtime/generation/orchestration.py src/ru_runtime/policy/style.py tests/test_system_pc_tools.py tests/test_reply_orchestration.py
git commit -m "feat: route chat volume markers through Core Audio"
```

---

### Task 3: Separate Agent tool-call and tool-result generation phases

**Files:**
- Modify: `D:\LLM\src\ru_runtime\policy\style.py`
- Modify: `D:\LLM\src\ru_runtime\generation\runner.py`
- Modify: `D:\LLM\src\ru_runtime\worker\v1\handlers.py`
- Modify: `D:\LLM\tests\test_runner_status.py`
- Modify: `D:\LLM\tests\test_worker_runtime_handlers.py`

**Interfaces:**
- `ModelRunner.generate_reply(..., structured_tools_only: bool = False, agent_follow_up: bool = False) -> str` selects one of three prompt/policy phases.
- `generate_once(..., phase: Literal["normal", "tool_request", "tool_result"]) -> str` maps `tool_request` to structured JSON-only tool instructions and `tool_result` to natural-language follow-up with all legacy markers disabled.

- [ ] **Step 1: Write failing phase tests**

Extend `test_runner_status.py` to assert:

```python
runner.generate_reply("ping", 128, 0.7, 40, structured_tools_only=True)
assert captured["allow_legacy_markers"] is False
assert captured["agent_follow_up"] is False

runner.generate_reply("ping", 128, 0.7, 40, agent_follow_up=True)
assert captured["allow_legacy_markers"] is False
assert captured["agent_follow_up"] is True
```

Add worker tests that feed a strict `system.volume` JSON reply, return a
successful fake tool result, and assert the second runner call is the
`tool_result` phase. Add a failed-result test asserting the worker emits a
truthful `CHAT_DONE` without a continuation that claims success.

- [ ] **Step 2: Run the focused tests and verify red**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_runner_status.py tests/test_worker_runtime_handlers.py
```

Expected result: failures because the runner has no follow-up phase and the
handler currently passes `structured_tools_only=True` to both generations.

- [ ] **Step 3: Add phase-specific prompt and runner behavior**

In `style.py`, keep the normal marker prompt in `RU_CLOUD_SYSTEM_PROMPT` and
add `RU_AGENT_SYSTEM_PROMPT` with this exact contract:

```text
For a desktop action, return only one JSON object with exactly these keys:
tool_id, tool_version, arguments.
For system.volume use tool_version 1.0.0 and arguments.operation from
get, set, increase, decrease, mute, unmute. Use arguments.level for set and
arguments.amount for increase/decrease. Do not emit [VOLUME: ...] markers.
If no action is needed, reply in natural Vietnamese without metadata.
```

Add `RU_AGENT_FOLLOW_UP_SYSTEM_PROMPT` that treats the structured tool result
as data, forbids claiming an unsuccessful operation, and permits a natural
Vietnamese answer. It contains no legacy action instructions.

In `runner.py`, pass `allow_legacy_markers=False` for both Agent phases and
select the corresponding prompt. Only the normal phase appends legacy VTS/
voice action instructions to the tool-generation prompt; the follow-up phase
may retain existing private VTS instructions.

In `handlers.py`, pass `phase="tool_request"` to the first Agent generation and
`phase="tool_result"` to the continuation. If the completed `system.volume`
result has a non-`succeeded` status, publish a bounded deterministic failure
reply instead of asking the model to describe a failed action.

- [ ] **Step 4: Run the selected worker tests and verify green**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_runner_status.py tests/test_worker_runtime_handlers.py tests/test_tool_calls.py
```

Expected result: all selected tests pass and the existing non-Agent chat path
still streams normally.

- [ ] **Step 5: Commit the Worker phase slice**

```powershell
git add -- src/ru_runtime/policy/style.py src/ru_runtime/generation/runner.py src/ru_runtime/worker/v1/handlers.py tests/test_runner_status.py tests/test_worker_runtime_handlers.py
git commit -m "fix: separate agent tool and reply phases"
```

---

### Task 4: Register and implement the Broker `system.volume` tool

**Files:**
- Create: `D:\LLM\backend-dotnet\RuAgent.Broker\Tools\Desktop\VolumeTools.cs`
- Create: `D:\LLM\backend-dotnet\RuAgent.Broker\Tools\Desktop\WindowsCoreAudioVolumeController.cs`
- Create: `D:\LLM\backend-dotnet\RuAgent.Broker.Tests\VolumeToolsTests.cs`
- Create: `D:\LLM\backend-dotnet\RuAgent.Broker.Tests\ToolCatalogTests.cs`
- Modify: `D:\LLM\backend-dotnet\RuAgent.Broker\Tools\ToolCatalog.cs`
- Modify: `D:\LLM\backend-dotnet\RuAgent.Broker\Tools\ToolExecutionService.cs`
- Modify: `D:\LLM\backend-dotnet\RuAgent.Broker\Runtime\BrokerHost.cs`

**Interfaces:**
- Add `IVolumeController` with `VolumeState GetState()`, `void SetLevel(int level)`, and `void SetMuted(bool muted)`.
- Add `VolumeTools.Execute(JsonObject arguments)` and a static `ToolManifest Manifest` for `system.volume@1.0.0`.
- Extend `BrokerHost.StartAsync` with an optional `IVolumeController?` injection while preserving existing callers.

- [ ] **Step 1: Write failing Broker tests**

Use a fake controller only. Cover exact `set` level 2, relative operations,
mute/unmute, invalid/range arguments, readback mismatch, stable failure output,
and the manifest's `system.write` scope, `Reversible` risk, bounded limits,
and no-network/no-workspace flags.

- [ ] **Step 2: Run the focused Broker tests and verify red**

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~VolumeTools|FullyQualifiedName~ToolCatalog"
```

Expected result: failures because the catalog, dispatch path, and volume
adapter do not exist yet.

- [ ] **Step 3: Implement the Broker tool and Windows adapter**

Register `system.volume` in `ToolCatalog` and dispatch it from
`ToolExecutionService` through an injected controller. Validate and normalize
the same request contract as Python, read Core Audio before and after writes,
and return `verified: true` only after exact readback. Keep failures bounded
and free of raw device names, COM details, credentials, or arguments.

Implement the production controller with Windows Core Audio COM against the
default render endpoint (`eRender`, `eMultimedia`). Do not use shell commands,
simulated key presses, elevation, or a test-only state store in the production
path.

- [ ] **Step 4: Run the selected Broker tests and verify green**

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~VolumeTools|FullyQualifiedName~ToolCatalog"
```

- [ ] **Step 5: Commit the Broker slice**

```powershell
git add -- backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs backend-dotnet/RuAgent.Broker/Tools/Desktop/WindowsCoreAudioVolumeController.cs backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs backend-dotnet/RuAgent.Broker.Tests/VolumeToolsTests.cs backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs
git commit -m "feat: add broker system volume tool"
```

---

### Task 5: Wire Host scope mapping and fake end-to-end Agent execution

**Files:**
- Modify: `D:\LLM\backend-dotnet\RuBackend\Conversation\AgentTurnCoordinator.cs`
- Create: `D:\LLM\backend-dotnet\RuBackend.Tests\Conversation\AgentTurnCoordinatorTests.cs`
- Modify: `D:\LLM\backend-dotnet\RuAgent.Broker.Tests\BrokerHostTests.cs`
- Modify: `D:\LLM\tests\test_worker_runtime_handlers.py`

- [ ] **Step 1: Write failing scope and phase tests**

Add a fake broker client test that asserts an Agent session receiving a
`system.volume` request gets `system.write`, and a denial test that proves the
tool cannot run when that scope is absent. Add a BrokerHost fake-controller
test for a successful WebSocket execution without touching real audio.

Keep the Python worker test asserting that a structured volume request is
handled in the `tool_request` phase and its continuation runs in the
`tool_result` phase.

- [ ] **Step 2: Run the focused Host/Broker tests and verify red**

```powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentTurnCoordinator"
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~BrokerHostTests"
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_runtime_handlers.py
```

Expected result: failures because `system.volume` is not mapped or granted
and the Broker host has no injectable controller path.

- [ ] **Step 3: Implement authorization and fake end-to-end wiring**

Grant `system.write` in the Agent session scope set and map
`system.volume` to `system.write` in `RequiredScope`. Preserve the existing
approval policy boundary for any future non-reversible system tools; this
local reversible tool does not require an approval prompt. Thread the optional
fake controller through the Broker host for tests while keeping the real
Windows adapter as the default.

- [ ] **Step 4: Run the selected tests and verify green**

```powershell
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~AgentTurnCoordinator"
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~BrokerHostTests"
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_worker_runtime_handlers.py tests/test_tool_calls.py
```

- [ ] **Step 5: Commit the authorization slice**

```powershell
git add -- backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs backend-dotnet/RuBackend.Tests/Conversation/AgentTurnCoordinatorTests.cs backend-dotnet/RuAgent.Broker.Tests/BrokerHostTests.cs tests/test_worker_runtime_handlers.py
git commit -m "feat: authorize agent volume execution"
```

---

### Task 6: Add a non-destructive Agent tool smoke matrix

**Files:**
- Modify: `D:\LLM\backend-dotnet\RuAgent.Broker.Tests\ToolCatalogTests.cs`
- Modify: `D:\LLM\tests\test_agent_harness.py`
- Modify: `D:\LLM\tests\test_harness_session.py`

- [ ] **Step 1: Write failing catalog and harness assertions**

Add metadata-only assertions that `system.volume` is registered with
`system.write`, `Reversible`, bounded timeout/output, no network, and no
workspace requirement. Keep the existing read-only IDs (`system.time`,
`system.status`, `app.list`, `files.list`, `files.read`) visible in the matrix,
and keep external or destructive tools in their separate policy categories.
Do not make the deterministic harness call a real volume adapter.

- [ ] **Step 2: Run the focused matrix and verify red**

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~ToolCatalog"
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_agent_harness.py tests/test_harness_session.py
```

- [ ] **Step 3: Implement the metadata-only smoke matrix**

Update the catalog and harness fixtures so the matrix covers registration,
scope, risk, network/workspace policy, and deterministic request/result shape
without changing the user's volume or depending on desktop state.

- [ ] **Step 4: Run the selected matrix and verify green**

```powershell
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo --filter "FullyQualifiedName~ToolCatalog"
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_agent_harness.py tests/test_harness_session.py
```

- [ ] **Step 5: Commit the smoke matrix**

```powershell
git add -- backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs tests/test_agent_harness.py tests/test_harness_session.py
git commit -m "test: add agent tool catalog smoke matrix"
```

---

### Task 7: Update operational docs and complete verification

**Files:**
- Modify: `D:\LLM\docs\operations\agent-tools.md`
- Modify: `D:\LLM\docs\operations\verification.md`
- Inspect: `D:\LLM\docs\operations\`, `D:\LLM\docs\superpowers\`

- [ ] **Step 1: Audit documentation for stale tool instructions**

Search the operational and implementation documents for the old keypress
volume behavior, the old fixed-step semantics, or claims that the Agent has
no system write tool. Update only documents made stale by this vertical slice;
preserve unrelated user edits.

- [ ] **Step 2: Document the shipped contract and acceptance boundary**

Document `system.volume@1.0.0`, the `system.write` reversible policy,
readback verification, bounded truthful failures, and the distinction between
fake-adapter automated tests and manual live Windows audio acceptance. Record
that normal chat markers are compatibility input, while Agent mode uses strict
structured tool requests followed by a separate natural-language result phase.

- [ ] **Step 3: Run targeted verification sequentially**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_volume_control.py tests/test_system_pc_tools.py tests/test_reply_orchestration.py tests/test_runner_status.py tests/test_worker_runtime_handlers.py tests/test_tool_calls.py tests/test_agent_harness.py tests/test_harness_session.py
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj -c Release --no-restore --nologo
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Release --no-restore --nologo
```

- [ ] **Step 4: Run the full Python suite and deterministic Harness**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
& D:\LLM\.venv\Scripts\python.exe scripts/aspnetcore_compatibility_harness.py --mode backend-deterministic --output D:\LLM\artifacts\aspnetcore\ru-harness-vnext.json
```

Keep heavyweight Python, .NET, and Harness checks sequential to avoid the
known Windows pagefile/OOM failure mode. Live microphone, Core Audio, and
VTube acceptance remain manual and must not be represented as automated pass.

- [ ] **Step 5: Review, commit, push, and hand off**

Run `git diff --check`, inspect the feature diff and status, confirm that only
feature files and the intended documentation changed, and leave unrelated
dirty paths untouched. Then commit and push the documentation update:

```powershell
git add -- docs/operations/agent-tools.md docs/operations/verification.md
git commit -m "docs: document verified agent volume tool"
git push origin main
```

Report the automated evidence separately from the manual live acceptance
steps: test an exact `set` request, a relative adjustment, mute/unmute, a
readback mismatch failure, and the normal-chat and Agent paths on the user's
Windows machine without claiming live success before the user observes it.

---
