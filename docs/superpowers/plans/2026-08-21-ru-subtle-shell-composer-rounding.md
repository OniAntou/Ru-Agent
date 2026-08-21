# Ru Subtle Shell and Composer Rounding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task with verification checkpoints.

**Goal:** Add a barely perceptible 6 px radius to the sidebar's right edge and the Chat input frame without changing layout or behavior.

**Architecture:** Reuse the existing `RuControlCornerRadius` token for the composer and use the explicit `0,6,6,0` geometry for the sidebar because its left edge is flush with the window. Lock both XAML contracts in the existing editorial test class; no new resource or runtime code is needed.

**Tech Stack:** WinUI 3, XAML, .NET 10, xUnit, PowerShell, `build.bat`.

---

### Task 1: Add the failing rounding contracts

**Files:**
- Modify: `D:\LLM\gui-winui3.Tests\EditorialReadingRoomContractTests.cs`

- [ ] **Step 1: Add the shell and composer visual contract**

Add this test after the existing Chat visual contracts:

```csharp
[Fact]
public void Shell_and_chat_use_subtle_shared_rounding()
{
    var shell = File.ReadAllText(FindSource("MainWindow.xaml"));
    var chat = File.ReadAllText(FindSource("ChatView.xaml"));

    Assert.Contains(
        "<Border Grid.Column=\"0\" Background=\"{StaticResource RuSidebarBrush}\" BorderThickness=\"0\" CornerRadius=\"0,6,6,0\">",
        shell,
        StringComparison.Ordinal);
    Assert.Contains(
        "x:Name=\"InputBorder\"",
        chat,
        StringComparison.Ordinal);
    Assert.Contains(
        "CornerRadius=\"{StaticResource RuControlCornerRadius}\"",
        chat,
        StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the focused test and verify the expected red failure**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests --filter FullyQualifiedName~EditorialReadingRoomContractTests --nologo
```

Expected: the new test fails because the sidebar has no `CornerRadius` and
`InputBorder` currently has `CornerRadius="0"`.

### Task 2: Apply the minimal XAML change

**Files:**
- Modify: `D:\LLM\gui-winui3\MainWindow.xaml:96`
- Modify: `D:\LLM\gui-winui3\Views\ChatView.xaml:190-195`

- [ ] **Step 1: Round only the approved surfaces**

Change the sidebar opening tag to:

```xml
<Border Grid.Column="0" Background="{StaticResource RuSidebarBrush}" BorderThickness="0" CornerRadius="0,6,6,0">
```

Change only the `InputBorder` radius to:

```xml
CornerRadius="{StaticResource RuControlCornerRadius}"
```

Leave navigation rows, session rows, buttons, message blocks, padding,
dimensions, colors, event handlers, and visual states unchanged.

- [ ] **Step 2: Run the focused test and verify green**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests --filter FullyQualifiedName~EditorialReadingRoomContractTests --nologo
```

Expected: all editorial contracts pass, including the new rounding contract.

### Task 3: Review docs and verify the complete build

**Files:**
- Review: `D:\LLM\docs\operations\runtime-behavior.md`
- Review: `D:\LLM\docs\superpowers\specs\2026-08-21-ru-subtle-shell-composer-rounding-design.md`

- [ ] **Step 1: Confirm documentation remains accurate**

The runtime behavior note needs no update because this is presentation-only.
The approved rounding spec remains the source of the exact 6 px contract.

- [ ] **Step 2: Run the full WinUI tests**

```powershell
dotnet test D:\LLM\gui-winui3.Tests --nologo
```

Expected: zero failures.

- [ ] **Step 3: Run the Release build**

```powershell
cmd /c D:\LLM\build.bat
```

Expected: exit code 0 and `Build complete`.

- [ ] **Step 4: Check the scoped diff**

```powershell
git diff --check
git status --short --branch
```

Expected: no whitespace errors and only the approved XAML/test changes before
commit.

### Task 4: Commit, push, and verify synchronization

**Files:**
- Commit: `D:\LLM\gui-winui3\MainWindow.xaml`
- Commit: `D:\LLM\gui-winui3\Views\ChatView.xaml`
- Commit: `D:\LLM\gui-winui3.Tests\EditorialReadingRoomContractTests.cs`
- Commit: `D:\LLM\docs\superpowers\plans\2026-08-21-ru-subtle-shell-composer-rounding.md`

- [ ] **Step 1: Commit the scoped implementation**

```powershell
git add D:\LLM\gui-winui3\MainWindow.xaml D:\LLM\gui-winui3\Views\ChatView.xaml D:\LLM\gui-winui3.Tests\EditorialReadingRoomContractTests.cs D:\LLM\docs\superpowers\plans\2026-08-21-ru-subtle-shell-composer-rounding.md
git commit -m "style: add subtle shell and composer rounding"
```

- [ ] **Step 2: Push `main`**

```powershell
git push origin main
```

- [ ] **Step 3: Verify the remote and worktree**

```powershell
$head = git rev-parse HEAD
$remote = git rev-parse origin/main
if ($head -ne $remote) { exit 1 }
git diff --check
git status --short --branch
```

Expected: `HEAD` equals `origin/main`, no diff-check output, and a clean
worktree. Manual visual acceptance remains user-run after restarting RuApp.
