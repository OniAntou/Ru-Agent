# Chat Welcome Stability Implementation Plan

> **Status:** Historical implementation record. Welcome-position stability
> shipped; current Agent-only shell behavior lives in
> `docs/operations/runtime-behavior.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Keep the empty-chat welcome content at the same screen position when the user switches from Chat to Coding Agent without sending a request.

**Architecture:** Treat Agent selection as mode state only. Track whether the Agent inline surface has a real activity to show, and let only that activity (request, run, trust, approval, error, or interaction) make the existing `Auto` composer row visible. The welcome layout, transport paths, permission semantics, and active-run panels remain unchanged.

**Tech Stack:** WinUI 3 XAML/C#, xUnit source-contract tests, PowerShell/.NET 10, repository documentation checks.

---

## Files and responsibilities

- Modify `gui-winui3/Views/ChatView.xaml.cs`: track explicit Agent activity and stop treating `_agentMode` alone as inline activity.
- Modify `gui-winui3.Tests/AgentSessionUiSourceContractTests.cs`: lock the idle-mode visibility contract and retain the request-path visibility contract.
- Modify `gui-winui3.Harness/UiAutomationScenario.cs`: enable-mode automation must assert the mode pill, then wait for activity only after sending a request.
- Modify `docs/operations/runtime-behavior.md`: document that idle Agent selection has no inline status row.
- Modify `docs/operations/agent-tools.md`: replace the idle status table row with the no-inline-panel behavior.
- Modify `docs/operations/verification.md`: remove the GUI harness requirement for an idle status string.
- Modify `tests/test_current_documentation.py`: assert the new runtime wording instead of the removed idle-copy requirement.
- Create/retain `docs/superpowers/specs/2026-08-28-chat-welcome-stability-design.md`: approved design decision and acceptance boundary.

### Task 1: Add the regression contract before changing production code

**Files:**
- Modify: `gui-winui3.Tests/AgentSessionUiSourceContractTests.cs`

- [x] **Step 1: Add a failing source-contract test for Agent idle mode.**

Add this fact to `AgentSessionUiSourceContractTests`:

```csharp
[Fact]
public void Selecting_agent_does_not_show_idle_inline_activity()
{
    string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

    int setModeStart = codeBehind.IndexOf("private void SetAgentMode", StringComparison.Ordinal);
    Assert.True(setModeStart >= 0, "SetAgentMode is missing.");
    int nextMethod = codeBehind.IndexOf("\n        private", setModeStart + 1, StringComparison.Ordinal);
    string setModeBody = nextMethod > setModeStart
        ? codeBehind[setModeStart..nextMethod]
        : codeBehind[setModeStart..];

    Assert.Contains("_agentActivityVisible = false", setModeBody, StringComparison.Ordinal);
    Assert.DoesNotContain("AgentStatusText.Text = \"Coding Agent sẵn sàng.\"", setModeBody, StringComparison.Ordinal);
    Assert.DoesNotContain("AgentInlinePanel.Visibility = Visibility.Visible", setModeBody, StringComparison.Ordinal);

    int sendStart = codeBehind.IndexOf("private async Task SendAgentMessageAsync", StringComparison.Ordinal);
    Assert.True(sendStart >= 0, "SendAgentMessageAsync is missing.");
    int nextSendMethod = codeBehind.IndexOf("\n        private async", sendStart + 1, StringComparison.Ordinal);
    string sendBody = nextSendMethod > sendStart
        ? codeBehind[sendStart..nextSendMethod]
        : codeBehind[sendStart..];

    Assert.Contains("ShowAgentInlineStatus(", sendBody, StringComparison.Ordinal);

    int helperStart = codeBehind.IndexOf("private void ShowAgentInlineStatus", StringComparison.Ordinal);
    Assert.True(helperStart >= 0, "ShowAgentInlineStatus is missing.");
    int nextHelperMethod = codeBehind.IndexOf("\n        private", helperStart + 1, StringComparison.Ordinal);
    string helperBody = nextHelperMethod > helperStart
        ? codeBehind[helperStart..nextHelperMethod]
        : codeBehind[helperStart..];
    Assert.Contains("_agentActivityVisible = true", helperBody, StringComparison.Ordinal);
    Assert.Contains("AgentInlinePanel.Visibility = Visibility.Visible", helperBody, StringComparison.Ordinal);
}
```

- [x] **Step 2: Run the focused test and confirm it fails for the old behavior.**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~AgentSessionUiSourceContractTests.Selecting_agent_does_not_show_idle_inline_activity
```

Expected result before the fix: `FAIL`, because `SetAgentMode` currently writes the idle copy and sets `AgentInlinePanel.Visibility = Visibility.Visible`.

### Task 2: Separate Agent mode selection from Agent activity visibility

**Files:**
- Modify: `gui-winui3/Views/ChatView.xaml.cs:70-330, 402-465, 515-711, 1180-1252`

- [x] **Step 1: Add one explicit activity flag and a helper.**

Add the field beside `_agentMode`:

```csharp
private bool _agentActivityVisible;
```

Add this helper beside `UpdateAgentModeVisuals`:

```csharp
private void ShowAgentInlineStatus(string status)
{
    _agentActivityVisible = true;
    AgentInlinePanel.Visibility = Visibility.Visible;
    AgentStatusText.Text = status;
}
```

- [x] **Step 2: Keep idle mode selection collapsed.**

In `SetAgentMode`, clear activity when switching into idle Agent mode and remove the ready-copy/visible assignment:

```csharp
_agentMode = enabled;
_agentActivityVisible = enabled && IsActiveAgentRunPending();
if (!enabled)
{
    _agentActivityVisible = false;
    _agentPreset = AgentPermissionPreset.Standard;
    _agentPresetSessionId = null;
    _agentPresetVersion = null;
    AgentPresetBox.SelectedIndex = 0;
    AgentInlinePanel.Visibility = Visibility.Collapsed;
}
else if (!_agentActivityVisible)
{
    AgentStatusText.Text = string.Empty;
    AgentInlinePanel.Visibility = Visibility.Collapsed;
}
```

The existing guard for an active run must remain fail-closed; its message should use `ShowAgentInlineStatus(...)` so disabling an active run still keeps the user-visible warning.

- [x] **Step 3: Make `UpdateAgentInlineState` depend on activity, not mode.**

In the `_agentController is null` and stale-session branches, replace the `_agentMode ? Visible : Collapsed` expression with `_agentActivityVisible ? Visible : Collapsed`, and clear `AgentStatusText` only when activity is false. Replace the `hasRunState` expression with:

```csharp
bool hasRunState = _agentActivityVisible
    || _agentController.IsBusy
    || _agentController.CanCancel
    || _agentController.HasPendingApproval
    || _agentController.NeedsWorkspaceTrust
    || _agentController.CanContinue;
```

This preserves active/terminal/error surfaces while making a mode-only selection layout-neutral.

- [x] **Step 4: Mark every real activity path before rendering it.**

Use `ShowAgentInlineStatus(...)` for the existing status-plus-visible pairs in:

- `OnWsDisconnected`;
- `SendCurrentMessageAsync` when the Agent controller is unavailable;
- `SendAgentMessageAsync` when a run is already pending;
- `SendAgentMessageAsync` immediately before `CreateAndRunAsync`;
- `RunAgentActionAsync`;
- `OnMicClick` while Agent mode is selected;
- `ShowAgentError` (compute the existing bounded message first, then call the helper).

Do not change the text filtering or transport calls. Keep `AgentInlinePanel.Visibility = Visibility.Visible` only where an activity has been marked, and preserve the existing trust/approval/Stop/Continue child visibility rules.

- [x] **Step 5: Reset the flag with the existing session reset.**

In `ResetAgentState`, set `_agentActivityVisible = false` next to `_agentMode = false`, before calling `UpdateAgentModeVisuals()` and collapsing the child panels.

- [x] **Step 6: Run the focused regression test and confirm it passes.**

Run the same command from Task 1. Expected result: `PASS`.

### Task 3: Align UI automation and current documentation

**Files:**
- Modify: `gui-winui3.Harness/UiAutomationScenario.cs:231-237`
- Modify: `docs/operations/runtime-behavior.md:189-192`
- Modify: `docs/operations/agent-tools.md:208-218`
- Modify: `docs/operations/verification.md:150-156`
- Modify: `tests/test_current_documentation.py:281-287`

- [x] **Step 1: Stop the GUI harness from requiring hidden idle text.**

Change the Agent enable wait from:

```csharp
bool enabled = await WaitUntilAsync(
    () => IsAgentModeActive(mode)
        && ContainsIgnoreCase(
            GetName(FindByAutomationId(AgentActivityAutomationId)),
            "Coding Agent sẵn sàng"),
    timeout);
```

to:

```csharp
bool enabled = await WaitUntilAsync(
    () => IsAgentModeActive(mode),
    timeout);
```

The existing later wait for `agent.activity` remains the proof that the inline surface appears after the request.

- [x] **Step 2: Update the runtime contract wording.**

Replace the idle-status paragraph in `runtime-behavior.md` with wording that the inline status appears only after an Agent request or activity needing user interaction; selecting Agent while idle does not add a status row and preserves the empty-chat welcome position. Keep the requested, terminal, and bounded error semantics unchanged.

- [x] **Step 3: Update the operations table and verification instructions.**

In `agent-tools.md`, change the idle row to:

```text
| Coding Agent selected, no request | No inline activity panel; the mode pill shows `Agent` |
```

In `verification.md`, replace the requirement for the idle copy with a requirement that the driver verifies the Agent mode pill and only expects `AgentStatusText` after sending an Agent request. Preserve the privacy and bounded-copy claims.

- [x] **Step 4: Update the documentation contract test.**

Replace the `"Coding Agent sẵn sàng."` entry in `tests/test_current_documentation.py` with a stable phrase present in the new runtime contract, such as `"does not add a composer status row"`. Keep the requested/terminal/bounded-state entries.

### Task 4: Verify the complete change and refresh the Release artifact

**Files:**
- No additional source files; inspect all changed documentation and generated artifacts.

- [x] **Step 1: Run the full GUI test project.**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo
```

Expected result: all GUI tests pass.

- [x] **Step 2: Build the WinUI app and GUI harness.**

Run sequentially:

```powershell
dotnet build D:\LLM\gui-winui3\RuApp.csproj -c Debug -p:Platform=x64 --no-restore --nologo
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Debug --no-restore --nologo
```

Expected result: both builds succeed with zero errors.

- [x] **Step 3: Run documentation and whitespace checks.**

Run:

```powershell
.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_current_documentation.py -q
git diff --check
```

Expected result: documentation tests pass and `git diff --check` reports no whitespace errors. Existing line-ending normalization warnings are not source failures.

- [x] **Step 4: Publish the official shortcut target after confirming no RuApp process is running.**

Read-only guard:

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'RuApp.exe'" |
    Select-Object ProcessId,ExecutablePath,CommandLine
```

If no process is running, publish:

```powershell
dotnet publish D:\LLM\gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore --nologo
```

Verify the official artifact timestamp and size:

```powershell
Get-Item D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe |
    Select-Object FullName,Length,LastWriteTime
```

- [ ] **Step 5: Perform the human visual acceptance handoff.**

Tell the user to open the existing shortcut, select `Coding Agent` without sending a request, and confirm that the welcome heading and suggestions remain at the same coordinates. Do not launch or control the app automatically; do not claim live visual acceptance from automated tests.

- [ ] **Step 6: Commit only the focused source/test/docs changes.**

Because the main checkout contains unrelated dirty work, inspect the diff and stage only the files listed in this plan:

```powershell
git diff -- gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/AgentSessionUiSourceContractTests.cs gui-winui3.Harness/UiAutomationScenario.cs docs/operations/runtime-behavior.md docs/operations/agent-tools.md docs/operations/verification.md tests/test_current_documentation.py
git add -- gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/AgentSessionUiSourceContractTests.cs gui-winui3.Harness/UiAutomationScenario.cs docs/operations/runtime-behavior.md docs/operations/agent-tools.md docs/operations/verification.md tests/test_current_documentation.py
git commit -m "fix(gui): keep welcome layout stable when selecting agent"
```

Do not stage or reset unrelated dirty files.

## Execution status (2026-08-28)

- Tasks 1-4, Steps 1-4 are complete. The focused source-contract test was intentionally RED before the production change and GREEN afterward; the full GUI suite is 301/301, the app and harness Debug builds succeed, the documentation suite is 12/12, and Release publish produced the official `RuApp.exe`.
- Task 4, Step 5 remains a manual handoff: the user should open the existing shortcut, select `Coding Agent` without sending a request, and confirm the welcome heading and suggestions keep the same coordinates.
- Task 4, Step 6 is intentionally deferred because the main checkout contains unrelated dirty changes in the same files; no unrelated files were staged, reset, or deleted.
