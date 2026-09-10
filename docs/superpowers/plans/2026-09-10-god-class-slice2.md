# God-class Slice 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Shrink ChatView/MainWindow God code-behinds and remove the stub Connectors Exe without changing behavior.

**Architecture:** Follow the repo's established slice pattern (`ChatAttachmentPolicy` = slice 1: pure static class in `RuApp.Services`, public for tests, ChatView delegates). Each slice moves verbatim logic, names magic as consts, and adds xunit coverage.

**Tech Stack:** .NET 10, WinUI 3 (`net10.0-windows10.0.26100.0`), xunit.

## Global Constraints

- Business logic unchanged: byte-identical outputs for moved functions.
- Do NOT touch `backend-dotnet/RuAgent.Cli/**` (user WIP, dirty in git).
- Do NOT touch `AgentRunCoordinator.cs` (needs dedicated session).
- Verify each task with its stated command before the next task.
- C# nullable + ImplicitUsings on; `Windows.UI.Color` available in both RuApp and gui-winui3.Tests (Windows TFM).

---

### Task 1: Connectors Exe to Library

**Files:**
- Modify: `backend-dotnet/RuAgent.Connectors/RuAgent.Connectors.csproj` (delete `<OutputType>Exe</OutputType>` line)
- Delete: `backend-dotnet/RuAgent.Connectors/Program.cs` (1-line `return 0;` stub; nothing references it)
- Test: `backend-dotnet/RuAgent.Connectors.Tests/` (adapter/policy tests only, no Program coverage)

**Interfaces:**
- Consumes: nothing (standalone project, only Protocol reference).
- Produces: same assembly as Library; test project resolves identically.

- [ ] **Step 1: Remove OutputType line**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
```

- [ ] **Step 2: Delete the stub**

Run: `git rm backend-dotnet/RuAgent.Connectors/Program.cs`

- [ ] **Step 3: Build + test**

Run: `dotnet test backend-dotnet/RuAgent.Connectors.Tests/RuAgent.Connectors.Tests.csproj --nologo -v q`
Expected: PASS (all adapter/policy tests green)

---

### Task 2: Extract AgentActivityPalette (TDD, slice 2)

**Files:**
- Create: `gui-winui3/Services/AgentActivityPalette.cs` (public static, moved verbatim + named consts)
- Modify: `gui-winui3/Views/ChatView.xaml.cs:1280-1322` (delete `SampleAuroraColor` + `LerpColor`, qualify call as `Services.AgentActivityPalette.SampleAuroraColor`)
- Test: `gui-winui3.Tests/AgentActivityPaletteTests.cs`

**Interfaces:**
- Consumes: `Windows.UI.Color` only.
- Produces: `public static Color SampleAuroraColor(double u, double center, Color baseColor)`, `public static Color LerpColor(Color from, Color to, double progress)`; consts `BeamRadius = 0.35`, `TintBlend = 0.85`.

- [ ] **Step 1: Write the failing test**

```csharp
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityPaletteTests
{
    [Fact]
    public void OutsideBeam_ReturnsBaseColor()
    {
        var baseColor = Windows.UI.Color.FromArgb(255, 10, 20, 30);
        Assert.Equal(baseColor, AgentActivityPalette.SampleAuroraColor(0.0, 0.9, baseColor));
    }

    [Fact]
    public void Lerp_EndpointsAreExact()
    {
        var from = Windows.UI.Color.FromArgb(255, 0, 0, 0);
        var to = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        Assert.Equal(from, AgentActivityPalette.LerpColor(from, to, 0.0));
        Assert.Equal(to, AgentActivityPalette.LerpColor(from, to, 1.0));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test gui-winui3.Tests --nologo -v q --filter FullyQualifiedName~AgentActivityPaletteTests`
Expected: FAIL (type not defined)

- [ ] **Step 3: Create palette with verbatim logic**

Move both methods unchanged; only additions: named consts `BeamRadius`, `TintBlend` replacing literals `0.35`, `0.85`.

- [ ] **Step 4: Rewire ChatView call site + run tests**

Run: same filter. Expected: PASS. Then full: `dotnet test gui-winui3.Tests --nologo -v q`. Expected: all green.

---

### Task 3: UiTiming constants

**Files:**
- Create: `gui-winui3/Services/UiTiming.cs` (public static class, `TimeSpan` + `double` consts)
- Modify: `gui-winui3/Views/ChatView.xaml.cs:185,290,641,1172,2440,2652`, `gui-winui3/MainWindow.xaml.cs:355,383,606,932,1262`
- Test: build + existing `gui-winui3.Tests` suite (behavior-identical literals).

**Interfaces:**
- Consumes: nothing.
- Produces: `TypingDebounce`, `SessionsPoll`, `ObservationPoll`, `ReconnectPoll`, `ModelSaveFailure`, `WaveFrame (~30fps)`, `AgentRetryDelay`, `StaleReactionSeconds = 15.0`, `ResizerActiveOpacity = 0.85`, `NarrowWidthThreshold` defer to `ResponsiveLayoutPolicy` tier (ChatView:2652 `< 900` becomes `GetForWidth(w).Tier == Narrow`, identical semantics).

- [ ] **Step 1: Create UiTiming with exact current values**

```csharp
namespace RuApp.Services;

public static class UiTiming
{
    public static readonly TimeSpan TypingDebounce = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan SessionsPoll = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ObservationPoll = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReconnectPoll = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ModelSaveFailure = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan AgentRetryDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan WaveFrame = TimeSpan.FromMilliseconds(33);
    public static readonly TimeSpan ChatFlash = TimeSpan.FromMilliseconds(120);
    public const double StaleReactionSeconds = 15.0;
    public const double ResizerActiveOpacity = 0.85;
}
```

- [ ] **Step 2: Replace literals at listed lines, keep values identical**
- [ ] **Step 3: Build + test**

Run: `dotnet build gui-winui3/RuApp.csproj --nologo -v q --no-restore` then full gui tests. Expected: 0 warnings, all green.

---

### Task 4: Deferred (no code)

- `RuAgent.Cli/*` God files: dirty user WIP, do not touch.
- `AgentRunCoordinator.cs` 47KB: dedicated session with conformance tests.
- `Models.cs` 737-line DTO bag: split per owner after slices land.
