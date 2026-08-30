# Chat Shell and Agent Composer Redesign Implementation Plan

> **Status:** Historical implementation record. The original Chat/Agent split
> was superseded by the Agent-only composer; current UI behavior lives in
> `docs/operations/runtime-behavior.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the WinUI shell quieter while exposing the existing Agent API/SSE workflow directly inside the Chat composer.

**Architecture:** Ordinary Chat remains on `ChatWebSocketClient`; Agent turns use the existing `AgentRunController` and `AgentRunClientAdapter`. The sidebar exposes only primary conversation surfaces and a Tools flyout. Agent status and approval controls stay outside the transcript and use bounded safe summaries.

**Tech Stack:** WinUI 3/XAML, .NET 10, xUnit source contracts, `RuAgent.Client` HTTP/SSE.

---

### Task 1: Lock the new shell and composer contracts

**Files:**
- Modify: `gui-winui3.Tests/AgentSessionUiSourceContractTests.cs`
- Modify: `gui-winui3.Tests/AgentApprovalUiContractTests.cs`
- Create: `gui-winui3.Tests/ChatShellUiContractTests.cs`

- [x] Replace the old phase-4 assertions that forbid Agent controls with assertions for `AgentModeButton`, `AgentModeChatOption`, `AgentModeAgentOption`, `AgentWorkspaceBox`, `AgentPresetBox`, `AgentStatusText`, and bounded trust/approval actions.
- [x] Add shell assertions that `MainWindow.xaml` contains `NavTools` and Tool-menu entries for `log`, `diagnostics`, `policy`, and `audit`, while not containing top-level `NavAgent`, `NavPolicy`, or `NavAudit` buttons.
- [x] Add routing source assertions that the Chat path calls `SendMessageAsync` and the Agent path creates `CreateAgentRunRequest` without calling Chat WebSocket for the same prompt.
- [x] Run the focused test filter and record the expected RED failures before implementation.

### Task 2: Add the compact Agent mode surface

**Files:**
- Modify: `gui-winui3/Views/ChatView.xaml`
- Modify: `gui-winui3/Views/ChatView.xaml.cs`
- Modify: `gui-winui3/MainWindow.xaml.cs`

- [x] Add a small mode pill inside `InputBorder` with accessible Chat/Agent choices, a separate Agent settings flyout for workspace and Standard/Full Access preset, and inline status/action cards.
- [x] Inject the existing `AgentRunController` into `ChatView.Initialize`; keep Agent HTTP/SSE orchestration out of `ChatWebSocketClient`.
- [x] Route typed Agent prompts to `/agent/v1/runs` with the active session, bounded workspace, selected preset, optional preset version, and a fresh idempotency key. Keep microphone input disabled in Agent mode.
- [x] Render only `AgentRunController.StatusText`, trust metadata, approval safe summary, and bounded event copy. Never render raw payloads, command arguments, stdout, or exception text in automation names.
- [x] Reset mode and local preset state on session replacement, WebSocket reset, disconnect, and disposal. Keep a run cancellable through the inline Stop action.

### Task 3: Simplify the shell navigation

**Files:**
- Modify: `gui-winui3/MainWindow.xaml`
- Modify: `gui-winui3/MainWindow.xaml.cs`

- [x] Remove the three top-level Agent/Policy/Audit buttons and add one `NavTools` button with a MenuFlyout containing Log, Diagnostics, Policy, and Audit entries.
- [x] Make `SetActiveNav` highlight `NavTools` for all technical screens and preserve narrow responsive label behavior.
- [x] Keep policy and audit controllers available through the Tools menu; remove the duplicate standalone Agent dashboard construction and route Agent only from Chat.
- [x] Preserve unrelated dirty backend and documentation changes; do not reset or checkout the whole worktree.

### Task 4: Verify and reconcile documents

**Files:**
- Review/modify: `README.md`
- Review/modify: `docs/README.md`
- Review/modify: `docs/operations/agent-tools.md`
- Review/modify: `docs/operations/runtime-behavior.md`
- Review/modify: `tests/test_current_documentation.py`

- [x] Update only claims that describe the desktop navigation or Agent entry point; retain the current `/agent/v1/*` API/SSE and current-user boundary wording.
- [x] Remove stale current-document claims that Agent is absent from Chat if any remain; retain dated design/plan history.
- [x] Run documentation tests and search for stale `NavAgent`/phase-4 no-Agent claims.

### Task 5: Build and test the GUI

**Files:**
- No additional production files unless verification exposes a concrete compile/test defect.

- [x] Run `dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo`.
- [x] Run `dotnet build D:\LLM\gui-winui3\RuApp.csproj -c Debug -p:Platform=x64 --no-restore`.
- [x] Inspect the changed documents again and report any remaining manual visual acceptance requirement without auto-controlling the user's desktop.
