# Temporarily Disable Local Screen Observation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the slow local Qwen3-VL observation path while keeping the structured cloud-reaction API available for a future vision model.

**Architecture:** The local provider and WinUI capture worker are removed. The FastAPI observation router remains the model-independent boundary; its lifecycle endpoints become explicit disabled compatibility responses, while `/observation/react` continues to accept structured observation payloads.

**Tech Stack:** Python 3.13, FastAPI/Pydantic, pytest, WinUI 3/C#/.NET 10, XAML, PowerShell documentation/scripts.

---

### Task 1: Lock the disabled backend contract with tests

**Files:**
- Modify: `D:/LLM/tests/test_server_api.py:232-260`
- Add: lifecycle assertions near the existing observation API tests in `D:/LLM/tests/test_server_api.py`

- [ ] **Step 1: Change the no-key vision expectation**

Change `test_observation_vision_config_local_without_key` to expect:

```python
assert config == {
    "status": "disabled",
    "engine": "local",
    "message": "Local screen observation is temporarily disabled while waiting for a replacement vision model.",
}
```

- [ ] **Step 2: Add lifecycle disabled-state coverage**

Add a test that calls `/observation/start`, `/observation/stop`, and `/observation/status` and asserts each response is HTTP 200, `status == "disabled"`, and `active is False`; the status response must also have `engine == "disabled"` and all numeric counters equal zero.

- [ ] **Step 3: Run the focused tests**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_server_api.py -k observation
```

Expected before implementation: the changed vision assertion and lifecycle test fail, proving the contract is not yet implemented.

### Task 2: Remove the local Python provider surface

**Files:**
- Delete: `D:/LLM/src/mini_gpt/local_vision_probe.py`
- Delete: `D:/LLM/tests/test_local_vision_probe.py`
- Delete: `D:/LLM/scripts/benchmark_local_vision.py`
- Delete: `D:/LLM/scripts/setup_local_vision.ps1`
- Delete: `D:/LLM/scripts/vision_model_manifest.json`

- [ ] **Step 1: Delete only the dedicated local-Qwen files**

Remove the five files above. Leave `D:/LLM/src/mini_gpt/screen_reaction.py`, `D:/LLM/scripts/probe_gemini_vision.py`, the downloaded `D:/LLM/models/vision` directory, and shared llama.cpp binaries untouched.

- [ ] **Step 2: Prove no active import remains**

Run:

```powershell
rg -n -S "local_vision_probe|VisionProbeConfig|benchmark_local_vision|setup_local_vision|vision_model_manifest|Qwen3VL-2B-Instruct" D:\LLM\src D:\LLM\scripts D:\LLM\tests
```

Expected: no matches in active source, scripts, or tests. Historical documentation will be updated in Task 5.

### Task 3: Make FastAPI observation lifecycle explicitly disabled

**Files:**
- Modify: `D:/LLM/src/mini_gpt/routers/observation.py:122-131`
- Modify: `D:/LLM/src/mini_gpt/routers/observation.py:261-297`

- [ ] **Step 1: Replace the local vision fallback**

When no Gemini credential exists, return the exact disabled response from Task 1 instead of `{"status":"ok","engine":"local"}`. Keep the Gemini-key branch unchanged.

- [ ] **Step 2: Replace local lifecycle state**

Make `/observation/start` and `/observation/stop` return `{"status":"disabled","active":False}` without toggling an active worker. Make `/observation/status` return disabled, inactive, zero counters, and `engine="disabled"`. Remove the unused `_OBSERVATION_ACTIVE` and `_OBSERVATION_LOCK` state if no remaining route needs it.

- [ ] **Step 3: Run the focused tests again**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q tests/test_server_api.py -k observation
```

Expected: all observation-focused tests pass, including the existing Gemini-key behavior and the new disabled lifecycle contract.

### Task 4: Remove the WinUI local capture and metrics client

**Files:**
- Delete: `D:/LLM/gui-winui3/Services/ScreenObserverService.cs`
- Modify: `D:/LLM/gui-winui3/MainWindow.xaml.cs:20,104,122,172-188`
- Modify: `D:/LLM/gui-winui3/Services/ApiService.cs:565-604`
- Modify: `D:/LLM/gui-winui3/Models/Models.cs:349-380`
- Modify: `D:/LLM/gui-winui3/Views/SettingsView.xaml:123-198`
- Modify: `D:/LLM/gui-winui3/Views/SettingsView.xaml.cs:14,40-55,118-168,407-417`

- [ ] **Step 1: Remove observer ownership from MainWindow**

Delete the `_observerService` field, construction, `SettingsView.Initialize` argument, and shutdown/dispose block. Change the settings initialization call to `Initialize(_apiService)`.

- [ ] **Step 2: Remove local-only API and DTO code**

Delete `GetObservationMetricsAsync`, `StartObservationAsync`, and `StopObservationAsync` from `ApiService.cs`; delete `ObservationMetricsDto` from `Models.cs`.

- [ ] **Step 3: Remove local event handlers from SettingsView**

Change `Initialize` to accept only `ApiService`. Remove the observer field, `SetupObservation`, metrics event handler, toggle click handler, UI-state updater, and observer unsubscribe in `Dispose`.

- [ ] **Step 4: Replace the misleading screen-observation controls**

Keep the observation settings PivotItem and Gemini credential row, but replace the capture button and metrics cards with a disabled notice stating that local screen observation is temporarily paused while waiting for a replacement vision model. The XAML must contain no `ToggleObservationButton`, `FrameCountText`, `VisionCountText`, `EmittedCountText`, `StaleDropsText`, or `LatencyText` names.

- [ ] **Step 5: Build the GUI**

Run:

```powershell
dotnet build D:\LLM\gui-winui3\RuApp.csproj -c Release -r win-x64 --no-restore -v:minimal
```

Expected: build succeeds with zero warnings and zero errors.

### Task 5: Update current documentation and remove active setup instructions

**Files:**
- Modify: `D:/LLM/README.md:148-189`
- Modify: `D:/LLM/TODO.md:82-97`

- [ ] **Step 1: Mark local observation paused**

Replace claims that the local Qwen3-VL pipeline is live or accepted with the paused state, explain that the structured backend/cloud route is retained, and state that the local model weights were not deleted in this maintenance change.

- [ ] **Step 2: Remove executable setup instructions**

Remove the README commands that run `setup_local_vision.ps1` and `benchmark_local_vision.py`.

- [ ] **Step 3: Check documents for stale local-provider references**

Run:

```powershell
rg -n -i "setup_local_vision|benchmark_local_vision|Qwen3-VL|Qwen3VL|Local Qwen|local vision" D:\LLM\README.md D:\LLM\TODO.md D:\LLM\docs
```

Expected: only an explicit historical/paused note remains; no current instruction claims the provider is available.

### Task 6: Run the complete verification gates

**Files:**
- Verify: all modified/deleted files above

- [ ] **Step 1: Compile Python**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m compileall -q D:\LLM\src D:\LLM\scripts
```

Expected: exit code 0.

- [ ] **Step 2: Run all Python tests**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
```

Expected: all remaining tests pass; the removed local-probe test file is no longer collected.

- [ ] **Step 3: Re-scan local-provider references and worktree**

Run:

```powershell
rg -n -S "local_vision_probe|benchmark_local_vision|setup_local_vision|vision_model_manifest|Qwen3VL-2B-Instruct" D:\LLM\src D:\LLM\scripts D:\LLM\tests D:\LLM\gui-winui3
git diff --check
git status --short
```

Expected: no active-code matches, no whitespace errors, and only the user-scoped changes plus the intended local-observation removal appear in the worktree.
