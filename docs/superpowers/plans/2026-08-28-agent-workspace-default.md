# Agent Workspace Default Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the WinUI Agent use the known project root when the workspace field is empty while retaining the API/Broker workspace boundary.

**Architecture:** Add a small pure resolver in `RuApp.Services` that prefers a trimmed user workspace and otherwise returns the injected default. `ChatView` keeps the default separate from the last selected value, resolves it immediately before creating `CreateAgentRunRequest`, and opens settings only when neither value exists. The backend contract and trust flow remain unchanged.

**Tech Stack:** WinUI 3/XAML, C#/.NET 10, xUnit, source contracts.

---

### Task 1: Lock workspace selection behavior with tests

**Files:**
- Create: `gui-winui3.Tests/AgentWorkspaceSelectionTests.cs`
- Modify: `gui-winui3.Tests/AgentSessionUiSourceContractTests.cs`

- [x] **Step 1: Add pure resolver tests before production code.**

Add tests for explicit input, empty input fallback, whitespace input fallback,
and both values empty returning `null`:

```csharp
[Theory]
[InlineData("D:\\custom", "D:\\project", "D:\\custom")]
[InlineData("", "D:\\project", "D:\\project")]
[InlineData("   ", "D:\\project", "D:\\project")]
[InlineData(null, "D:\\project", "D:\\project")]
[InlineData("", "", null)]
public void Resolve_prefers_requested_workspace_and_falls_back_to_default(
    string? requested,
    string? fallback,
    string? expected)
{
    Assert.Equal(expected, AgentWorkspaceSelection.Resolve(requested, fallback));
}
```

Extend the existing source contract to require `AgentWorkspaceSelection.Resolve`,
`_defaultAgentWorkspace`, and an optional-workspace label/placeholder, while
forbidding the old direct empty-workspace exception copy.

- [x] **Step 2: Run the focused tests and confirm RED.**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo --filter "FullyQualifiedName~AgentWorkspaceSelectionTests|FullyQualifiedName~AgentSessionUiSourceContractTests.Chat_code_behind_routes_agent_prompt_through_the_shared_controller"
```

Expected result: compile/test failure because `AgentWorkspaceSelection` and the
new Chat integration markers do not exist yet.

### Task 2: Implement implicit defaulting without weakening the boundary

**Files:**
- Create: `gui-winui3/Services/AgentWorkspaceSelection.cs`
- Modify: `gui-winui3/Views/ChatView.xaml.cs:66-132, 590-608`
- Modify: `gui-winui3/Views/ChatView.xaml:477-481`

- [x] **Step 1: Add the minimal pure resolver.**

Create an internal helper with this behavior:

```csharp
namespace RuApp.Services;

internal static class AgentWorkspaceSelection
{
    public static string? Resolve(string? requestedWorkspace, string? defaultWorkspace)
    {
        if (!string.IsNullOrWhiteSpace(requestedWorkspace))
        {
            return requestedWorkspace.Trim();
        }

        return string.IsNullOrWhiteSpace(defaultWorkspace)
            ? null
            : defaultWorkspace.Trim();
    }
}
```

- [x] **Step 2: Keep an injected default separate from the editable value.**

Add a nullable `_defaultAgentWorkspace` field. In `Initialize`, set it to the
trimmed `defaultAgentWorkspace` or `Environment.CurrentDirectory`, then seed the
existing `_agentWorkspace` and `AgentWorkspaceBox.Text` from that value. Do not
change session/preset reset semantics.

- [x] **Step 3: Resolve the workspace immediately before run creation.**

Replace the direct `AgentWorkspaceBox.Text` empty check in
`SendAgentMessageAsync` with:

```csharp
string? workspace = AgentWorkspaceSelection.Resolve(
    AgentWorkspaceBox.Text,
    _defaultAgentWorkspace);
if (workspace is null)
{
    AgentSettingsFlyout.ShowAt(AgentSettingsButton);
    AgentWorkspaceBox.Focus(FocusState.Programmatic);
    ShowAgentInlineStatus("Chưa xác định được thư mục làm việc cho Coding Agent.");
    return;
}

_agentWorkspace = workspace;
```

The resolved string still flows into the existing `CreateAgentRunRequest`; do
not remove backend canonicalization, workspace trust, preset checks, or error
mapping.

- [x] **Step 4: Make the settings copy reflect optional input.**

Change the workspace TextBox header to `Thư mục làm việc (tuỳ chọn)` and its
placeholder to `Để trống để dùng project root`. The separate settings flyout
and trust card remain available for an explicit alternate workspace.

- [x] **Step 5: Run the focused tests and confirm GREEN.**

Run the Task 1 command again. Expected result: all resolver and source-contract
tests pass.

### Task 3: Verify the existing Agent surface and documentation

**Files:**
- Review: `docs/operations/runtime-behavior.md`
- Review: `docs/operations/agent-tools.md`
- Review: `README.md`

- [x] **Step 1: Confirm current docs still state the backend invariant.**

Keep the claims that Agent runs are trusted-root-bounded by default and
trust-gated; document explicit host-approved external-directory grants where
the implementation supports them. Update only if any sentence says the user
must manually type a workspace before every run. The intended claim is that UI
defaults to project root while the API still requires canonical
`workspace_path`.

- [x] **Step 2: Run repository verification sequentially.**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo
dotnet build D:\LLM\gui-winui3\RuApp.csproj -c Debug -p:Platform=x64 --no-restore --nologo
.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_current_documentation.py -q
git diff --check
```

Expected result: all GUI/documentation tests pass, the app builds cleanly, and
`git diff --check` exits 0 apart from known line-ending normalization warnings.

- [x] **Step 3: Preserve unrelated dirty work and report manual acceptance.**

Inspect `git status --short`; do not reset, delete, or stage unrelated files.
Tell the user to clear the workspace field, send an Agent prompt, and verify
the existing trust flow appears only when the default workspace is not trusted.
