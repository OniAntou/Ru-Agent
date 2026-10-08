---
status: pending
date: 2026-10-08
topic: right-side-panel
---

# Right Side Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the right side of RuApp into a two-tier, DSH-style surface: the floating summary card stays (basic info), a chevron opens a full-height details panel with closable Changes/Tasks tabs, resize and fullscreen, and one title-bar toggle next to the caption buttons hides/restores the whole surface.

**Architecture:** One `AgentSidePanelControl` owns both hosts (card and panel) and only renders. `AgentSidePanelPolicy` (pure, tested) owns the per-session state machine and the width clamp. `ChatView.SidePanel.cs` translates control events into policy calls, applies the returned state, and owns the layout reserve; `MainWindow` hosts the title-bar toggle and its WinUI passthrough region. Design reference: `docs/plans/2026-10-08-right-side-panel-design.md`.

**Tech Stack:** .NET 10, WinUI 3 (Windows App SDK), xUnit source-contract tests (`GuiSource` reads markup and code by member body), PowerShell test matrix.

**Working rules for the executor:**

- Work in `D:/LLM` on branch `dev`. No worktree.
- Run one matrix at a time; `scripts/test_dotnet.ps1` takes a machine-wide lock (exit 75 means another run holds it).
- Stage only the files listed per task. The working tree already carries two modified doc files (`docs/operations/agent-tools.md`, `docs/operations/coding-agent-capabilities.md`) from the previous GUI change; Task 8 edits both again and commits their working-tree content as part of the docs update. Do not stage anything else.
- Comments explain *why*, never *what*; keep functions short; no magic numbers.
- Never weaken a failing contract test to go green; update it only where the decision intentionally changed.

---

### Task 1: Side panel policy (state machine)

**Files:**
- Create: `gui-winui3/Services/AgentSidePanelPolicy.cs`
- Test: `gui-winui3.Tests/AgentSidePanelPolicyTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `gui-winui3.Tests/AgentSidePanelPolicyTests.cs`:

```csharp
using System.Linq;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentSidePanelPolicyTests
{
    [Fact]
    public void Defaults_are_the_card_with_both_tabs_open()
    {
        var policy = new AgentSidePanelPolicy();

        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s1"));
        Assert.Equal(new[] { SidePanelTabKind.Changes, SidePanelTabKind.Tasks }, policy.GetOpenTabs("s1"));
        Assert.Equal(SidePanelTabKind.Changes, policy.GetActiveTab("s1"));
        Assert.False(policy.IsFullscreen("s1"));
    }

    [Fact]
    public void Expand_and_collapse_round_trip()
    {
        var policy = new AgentSidePanelPolicy();

        policy.ExpandToPanel("s1");
        Assert.Equal(AgentSidePanelMode.Panel, policy.GetMode("s1"));

        policy.CollapseToCard("s1");
        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s1"));
    }

    [Fact]
    public void Toggle_hides_and_restores_the_previous_mode()
    {
        var policy = new AgentSidePanelPolicy();

        policy.ToggleVisibility("s1");
        Assert.Equal(AgentSidePanelMode.Hidden, policy.GetMode("s1"));
        policy.ToggleVisibility("s1");
        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s1"));

        policy.ExpandToPanel("s1");
        policy.ToggleVisibility("s1");
        Assert.Equal(AgentSidePanelMode.Hidden, policy.GetMode("s1"));
        policy.ToggleVisibility("s1");
        Assert.Equal(AgentSidePanelMode.Panel, policy.GetMode("s1"));
    }

    [Fact]
    public void Hiding_from_fullscreen_restores_fullscreen()
    {
        var policy = new AgentSidePanelPolicy();
        policy.ExpandToPanel("s1");
        policy.ToggleFullscreen("s1");

        policy.ToggleVisibility("s1");
        policy.ToggleVisibility("s1");

        Assert.Equal(AgentSidePanelMode.Panel, policy.GetMode("s1"));
        Assert.True(policy.IsFullscreen("s1"));
    }

    [Fact]
    public void Closing_the_active_tab_selects_the_first_open_tab()
    {
        var policy = new AgentSidePanelPolicy();

        policy.CloseTab("s1", SidePanelTabKind.Changes);

        Assert.Equal(new[] { SidePanelTabKind.Tasks }, policy.GetOpenTabs("s1"));
        Assert.Equal(SidePanelTabKind.Tasks, policy.GetActiveTab("s1"));
    }

    [Fact]
    public void Closing_the_last_tab_collapses_to_the_card()
    {
        var policy = new AgentSidePanelPolicy();
        policy.ExpandToPanel("s1");

        policy.CloseTab("s1", SidePanelTabKind.Changes);
        policy.CloseTab("s1", SidePanelTabKind.Tasks);

        Assert.Empty(policy.GetOpenTabs("s1"));
        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s1"));
    }

    [Fact]
    public void Expanding_with_no_open_tabs_seeds_changes()
    {
        var policy = new AgentSidePanelPolicy();
        policy.CloseTab("s1", SidePanelTabKind.Changes);
        policy.CloseTab("s1", SidePanelTabKind.Tasks);

        policy.ExpandToPanel("s1");

        Assert.Equal(new[] { SidePanelTabKind.Changes }, policy.GetOpenTabs("s1"));
        Assert.Equal(SidePanelTabKind.Changes, policy.GetActiveTab("s1"));
    }

    [Fact]
    public void Opening_a_closed_tab_adds_and_activates_it()
    {
        var policy = new AgentSidePanelPolicy();
        policy.CloseTab("s1", SidePanelTabKind.Tasks);

        policy.OpenTab("s1", SidePanelTabKind.Tasks);

        Assert.Contains(SidePanelTabKind.Tasks, policy.GetOpenTabs("s1"));
        Assert.Equal(SidePanelTabKind.Tasks, policy.GetActiveTab("s1"));
    }

    [Fact]
    public void Closing_resets_fullscreen()
    {
        var policy = new AgentSidePanelPolicy();
        policy.ExpandToPanel("s1");
        policy.ToggleFullscreen("s1");

        policy.CollapseToCard("s1");

        Assert.False(policy.IsFullscreen("s1"));
    }

    [Fact]
    public void Width_clamp_handles_bounds_and_non_finite_values()
    {
        Assert.Equal(280, AgentSidePanelPolicy.ClampWidth(10));
        Assert.Equal(560, AgentSidePanelPolicy.ClampWidth(9_999));
        Assert.Equal(420, AgentSidePanelPolicy.ClampWidth(420));
        Assert.Equal(AgentSidePanelPolicy.DefaultWidth, AgentSidePanelPolicy.ClampWidth(double.NaN));
        Assert.Equal(AgentSidePanelPolicy.DefaultWidth, AgentSidePanelPolicy.ClampWidth(double.PositiveInfinity));
    }

    [Fact]
    public void Retention_evicts_the_least_recently_touched_session()
    {
        var policy = new AgentSidePanelPolicy();
        for (var index = 0; index < AgentSidePanelPolicy.MaxRetainedSessions; index++)
        {
            policy.ExpandToPanel($"s{index}");
        }

        policy.ExpandToPanel("overflow");

        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s0"));
        Assert.Equal(AgentSidePanelMode.Panel, policy.GetMode("s1"));
        Assert.Equal(AgentSidePanelMode.Panel, policy.GetMode("overflow"));
    }

    [Fact]
    public void Blank_session_ids_are_ignored()
    {
        var policy = new AgentSidePanelPolicy();

        policy.ExpandToPanel(null);
        policy.ExpandToPanel("  ");

        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode(null));
        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("  "));
    }

    [Fact]
    public void Removing_a_session_drops_its_state()
    {
        var policy = new AgentSidePanelPolicy();
        policy.ExpandToPanel("s1");

        policy.Remove("s1");

        Assert.Equal(AgentSidePanelMode.Card, policy.GetMode("s1"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AgentSidePanelPolicyTests"`
Expected: FAIL to compile — `AgentSidePanelPolicy` does not exist.

- [ ] **Step 3: Implement the policy**

Create `gui-winui3/Services/AgentSidePanelPolicy.cs`:

```csharp
namespace RuApp.Services;

/// <summary>
/// Which tab of the right side surface a session has open.
/// </summary>
public enum SidePanelTabKind
{
    Changes,
    Tasks,
}

/// <summary>
/// The right side surface's shape for one session: the floating summary card,
/// the full-height details panel, or nothing.
/// </summary>
public enum AgentSidePanelMode
{
    Card,
    Panel,
    Hidden,
}

/// <summary>
/// The per-session state of the right side surface: which host is showing,
/// which tabs are open, which one is active, the fullscreen flag, and the width
/// clamp. One bounded entry per session, so a closed tab or a hidden surface
/// survives a session switch without outliving its session.
/// </summary>
public sealed class AgentSidePanelPolicy
{
    public const int MaxRetainedSessions = 8;
    public const double MinimumWidth = 280;
    public const double MaximumWidth = 560;
    public const double DefaultWidth = 360;

    private sealed class SessionState
    {
        internal AgentSidePanelMode Mode = AgentSidePanelMode.Card;
        internal AgentSidePanelMode LastVisibleMode = AgentSidePanelMode.Card;
        internal List<SidePanelTabKind> OpenTabs = new() { SidePanelTabKind.Changes, SidePanelTabKind.Tasks };
        internal SidePanelTabKind ActiveTab = SidePanelTabKind.Changes;
        internal bool Fullscreen;
        internal long Touched;
    }

    private readonly Dictionary<string, SessionState> _bySession = new(StringComparer.Ordinal);
    private long _touchCounter;

    /// <summary>Clamps a stored or dragged width; a non-finite value reads as the default.</summary>
    public static double ClampWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinimumWidth, MaximumWidth) : DefaultWidth;

    public AgentSidePanelMode GetMode(string? sessionId) =>
        Find(sessionId)?.Mode ?? AgentSidePanelMode.Card;

    public IReadOnlyList<SidePanelTabKind> GetOpenTabs(string? sessionId) =>
        Find(sessionId)?.OpenTabs.ToArray() ?? DefaultOpenTabs();

    public SidePanelTabKind GetActiveTab(string? sessionId) =>
        Find(sessionId)?.ActiveTab ?? SidePanelTabKind.Changes;

    public bool IsFullscreen(string? sessionId) => Find(sessionId)?.Fullscreen ?? false;

    public void ExpandToPanel(string? sessionId)
    {
        var state = GetOrCreate(sessionId);
        if (state is null)
        {
            return;
        }

        if (state.OpenTabs.Count == 0)
        {
            state.OpenTabs.Add(SidePanelTabKind.Changes);
            state.ActiveTab = SidePanelTabKind.Changes;
        }

        state.Mode = AgentSidePanelMode.Panel;
        state.LastVisibleMode = AgentSidePanelMode.Panel;
    }

    public void CollapseToCard(string? sessionId)
    {
        var state = GetOrCreate(sessionId);
        if (state is null)
        {
            return;
        }

        state.Mode = AgentSidePanelMode.Card;
        state.LastVisibleMode = AgentSidePanelMode.Card;
        state.Fullscreen = false;
    }

    public void ToggleVisibility(string? sessionId)
    {
        var state = GetOrCreate(sessionId);
        if (state is null)
        {
            return;
        }

        if (state.Mode == AgentSidePanelMode.Hidden)
        {
            state.Mode = state.LastVisibleMode;
            return;
        }

        state.LastVisibleMode = state.Mode;
        state.Mode = AgentSidePanelMode.Hidden;
    }

    public void ActivateTab(string? sessionId, SidePanelTabKind kind)
    {
        var state = GetOrCreate(sessionId);
        if (state is null || !state.OpenTabs.Contains(kind))
        {
            return;
        }

        state.ActiveTab = kind;
    }

    public void CloseTab(string? sessionId, SidePanelTabKind kind)
    {
        var state = GetOrCreate(sessionId);
        if (state is null || !state.OpenTabs.Remove(kind))
        {
            return;
        }

        if (state.ActiveTab == kind)
        {
            state.ActiveTab = state.OpenTabs.Count > 0 ? state.OpenTabs[0] : SidePanelTabKind.Changes;
        }

        if (state.OpenTabs.Count == 0)
        {
            state.Mode = AgentSidePanelMode.Card;
            state.LastVisibleMode = AgentSidePanelMode.Card;
            state.Fullscreen = false;
        }
    }

    public void OpenTab(string? sessionId, SidePanelTabKind kind)
    {
        var state = GetOrCreate(sessionId);
        if (state is null)
        {
            return;
        }

        if (!state.OpenTabs.Contains(kind))
        {
            state.OpenTabs.Add(kind);
        }

        state.ActiveTab = kind;
    }

    public void ToggleFullscreen(string? sessionId)
    {
        var state = GetOrCreate(sessionId);
        if (state is null)
        {
            return;
        }

        state.Fullscreen = !state.Fullscreen;
    }

    public void Remove(string? sessionId)
    {
        if (IsUsableSessionId(sessionId))
        {
            _bySession.Remove(sessionId!);
        }
    }

    private SessionState? Find(string? sessionId) =>
        IsUsableSessionId(sessionId) && _bySession.TryGetValue(sessionId!, out var state) ? state : null;

    private SessionState? GetOrCreate(string? sessionId)
    {
        if (!IsUsableSessionId(sessionId))
        {
            return null;
        }

        if (!_bySession.TryGetValue(sessionId!, out var state))
        {
            EvictOldestWhileFull();
            state = new SessionState { Touched = ++_touchCounter };
            _bySession[sessionId!] = state;
            return state;
        }

        state.Touched = ++_touchCounter;
        return state;
    }

    private void EvictOldestWhileFull()
    {
        while (_bySession.Count >= MaxRetainedSessions)
        {
            string? oldestKey = null;
            long oldestTouch = long.MaxValue;
            foreach (var (key, value) in _bySession)
            {
                if (value.Touched < oldestTouch)
                {
                    oldestTouch = value.Touched;
                    oldestKey = key;
                }
            }

            if (oldestKey is null)
            {
                return;
            }

            _bySession.Remove(oldestKey);
        }
    }

    private static bool IsUsableSessionId(string? sessionId) => !string.IsNullOrWhiteSpace(sessionId);

    private static IReadOnlyList<SidePanelTabKind> DefaultOpenTabs() =>
        new[] { SidePanelTabKind.Changes, SidePanelTabKind.Tasks };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AgentSidePanelPolicyTests"`
Expected: PASS — 13 passed, 0 failed.

- [ ] **Step 5: Commit**

```powershell
git add gui-winui3/Services/AgentSidePanelPolicy.cs gui-winui3.Tests/AgentSidePanelPolicyTests.cs
git commit -m "feat(gui): per-session side surface policy"
```

---

### Task 2: Persist the side panel width

**Files:**
- Create: `gui-winui3/Services/SidePanelPreferenceStore.cs`
- Test: `gui-winui3.Tests/SidePanelPreferenceStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `gui-winui3.Tests/SidePanelPreferenceStoreTests.cs`:

```csharp
using System;
using System.IO;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SidePanelPreferenceStoreTests
{
    [Fact]
    public void Save_then_load_round_trips_the_width()
    {
        var path = TempPath();
        try
        {
            Assert.True(new SidePanelPreferenceStore(path).TrySave(420));
            Assert.Equal(420, new SidePanelPreferenceStore(path).Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void A_missing_file_reads_as_the_default()
    {
        var path = TempPath();
        Assert.Equal(AgentSidePanelPolicy.DefaultWidth, new SidePanelPreferenceStore(path).Load());
    }

    [Fact]
    public void A_garbage_file_reads_as_the_default()
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            Assert.Equal(AgentSidePanelPolicy.DefaultWidth, new SidePanelPreferenceStore(path).Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void An_oversized_file_reads_as_the_default()
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new string(' ', 65 * 1024));
            Assert.Equal(AgentSidePanelPolicy.DefaultWidth, new SidePanelPreferenceStore(path).Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Stored_widths_are_clamped_on_save_and_load()
    {
        var path = TempPath();
        try
        {
            Assert.True(new SidePanelPreferenceStore(path).TrySave(9_999));
            Assert.Equal(AgentSidePanelPolicy.MaximumWidth, new SidePanelPreferenceStore(path).Load());

            File.WriteAllText(path, "{\"width\":10}");
            Assert.Equal(AgentSidePanelPolicy.MinimumWidth, new SidePanelPreferenceStore(path).Load());
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void Non_finite_widths_are_refused()
    {
        var store = new SidePanelPreferenceStore(TempPath());
        Assert.False(store.TrySave(double.NaN));
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ru-side-panel-tests", Guid.NewGuid().ToString("N"), "side-panel.json");

    private static void Cleanup(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~SidePanelPreferenceStoreTests"`
Expected: FAIL to compile — `SidePanelPreferenceStore` does not exist.

- [ ] **Step 3: Implement the store**

Create `gui-winui3/Services/SidePanelPreferenceStore.cs`:

```csharp
using System.Text.Json;

namespace RuApp.Services;

/// <summary>
/// The persisted width of the right side panel: one bounded JSON file under the
/// app's data root, written atomically. A missing, oversized, or unreadable
/// file reads as the default width, and a stored value is clamped before it is
/// handed out, so a hand-edited file cannot size the panel past its bounds.
/// </summary>
public sealed class SidePanelPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public SidePanelPreferenceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "side-panel.json");
    }

    public double Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return AgentSidePanelPolicy.DefaultWidth;
            }

            var bytes = PreferenceFile.ReadOrNull(_filePath);
            if (bytes is null)
            {
                return AgentSidePanelPolicy.DefaultWidth;
            }

            var preferences = JsonSerializer.Deserialize<SidePanelPreferences>(bytes, JsonOptions);
            return AgentSidePanelPolicy.ClampWidth(preferences?.Width ?? AgentSidePanelPolicy.DefaultWidth);
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            JsonException or
            InvalidDataException or
            NotSupportedException or
            ArgumentException)
        {
            return AgentSidePanelPolicy.DefaultWidth;
        }
    }

    public bool TrySave(double width)
    {
        if (!double.IsFinite(width))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(
                new SidePanelPreferences(AgentSidePanelPolicy.ClampWidth(width)),
                JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            NotSupportedException or
            ArgumentException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private sealed record SidePanelPreferences(double? Width);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~SidePanelPreferenceStoreTests"`
Expected: PASS — 6 passed, 0 failed.

- [ ] **Step 5: Commit**

```powershell
git add gui-winui3/Services/SidePanelPreferenceStore.cs gui-winui3.Tests/SidePanelPreferenceStoreTests.cs
git commit -m "feat(gui): persist the side panel width"
```

---

### Task 3: Project session edits for the Changes tab

**Files:**
- Create: `gui-winui3/Models/ChangesTabRow.cs`
- Create: `gui-winui3/Services/ChangesTabProjection.cs`
- Test: `gui-winui3.Tests/ChangesTabProjectionTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `gui-winui3.Tests/ChangesTabProjectionTests.cs`:

```csharp
using System.Linq;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChangesTabProjectionTests
{
    [Fact]
    public void Non_edit_activities_are_ignored()
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("a.txt"),
            Activity(AgentActivityState.Completed, AgentActivityKind.Search, symbol: "query"),
        });

        Assert.Single(projection.Rows);
        Assert.Equal("a.txt", projection.Rows[0].FileName);
    }

    [Fact]
    public void Rows_are_newest_first()
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("first.txt"),
            Edit("second.txt"),
            Edit("third.txt"),
        });

        Assert.Equal(
            new[] { "third.txt", "second.txt", "first.txt" },
            projection.Rows.Select(row => row.FileName));
    }

    [Fact]
    public void The_row_cap_keeps_the_newest_rows_and_totals_cover_everything()
    {
        var edits = Enumerable.Range(0, ChangesTabProjection.MaxRows + 5)
            .Select(index => Edit($"file-{index}.txt", added: 1, removed: 2))
            .ToArray();

        var projection = ChangesTabProjection.Project(edits);

        Assert.Equal(ChangesTabProjection.MaxRows, projection.Rows.Count);
        Assert.Equal($"file-{ChangesTabProjection.MaxRows + 4}.txt", projection.Rows[0].FileName);
        Assert.Equal(ChangesTabProjection.MaxRows + 5, projection.Added);
        Assert.Equal((ChangesTabProjection.MaxRows + 5) * 2, projection.Removed);
    }

    [Fact]
    public void Rows_carry_the_preview_and_the_expansion_starts_folded()
    {
        var projection = ChangesTabProjection.Project(new[]
        {
            Edit("ChatView.xaml", added: 3, removed: 1, lineStart: 42, detail: "@@ old-line ==> new-line"),
        });

        var row = projection.Rows[0];
        Assert.Equal(3, row.Added);
        Assert.Equal(1, row.Removed);
        Assert.Equal(42, row.StartLine);
        Assert.Equal("old-line", row.BeforeText);
        Assert.Equal("new-line", row.AfterText);
        Assert.False(row.IsExpanded);

        row.IsExpanded = true;
        Assert.Equal("\uE70D", row.ExpansionGlyph);
    }

    [Fact]
    public void Null_deltas_read_as_zero()
    {
        var projection = ChangesTabProjection.Project(new[] { Edit("empty.txt") });

        Assert.Equal(0, projection.Rows[0].Added);
        Assert.Equal(0, projection.Rows[0].Removed);
    }

    private static AgentActivityItem Edit(
        string fileName,
        int? added = null,
        int? removed = null,
        int? lineStart = null,
        string? detail = null) =>
        new(Activity(
            AgentActivityState.Completed,
            AgentActivityKind.Edit,
            fileName: fileName,
            lineStart: lineStart,
            added: added,
            removed: removed,
            detail: detail));

    private static AgentActivityModel Activity(
        AgentActivityState state,
        AgentActivityKind kind,
        string? fileName = null,
        int? lineStart = null,
        int? added = null,
        int? removed = null,
        string? symbol = null,
        string? detail = null) =>
        new(
            "act_22222222222222222222222222222222",
            kind,
            state,
            fileName,
            lineStart,
            null,
            symbol,
            null,
            added,
            removed,
            null,
            "succeeded",
            null,
            null,
            1,
            detail,
            Todos: null);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~ChangesTabProjectionTests"`
Expected: FAIL to compile — `ChangesTabProjection`/`ChangesTabRow` do not exist.

- [ ] **Step 3: Implement the row model**

Create `gui-winui3/Models/ChangesTabRow.cs`:

```csharp
using System.ComponentModel;

namespace RuApp.Models;

/// <summary>
/// One row of the side panel's Changes tab: a projection of one edit activity,
/// with its own expand state so opening a diff in the panel never folds the
/// transcript's copy of the same edit.
/// </summary>
public sealed class ChangesTabRow : INotifyPropertyChanged
{
    public ChangesTabRow(
        string fileName,
        int added,
        int removed,
        string beforeText,
        string afterText,
        int? startLine,
        string safeCode)
    {
        FileName = fileName;
        Added = added;
        Removed = removed;
        BeforeText = beforeText;
        AfterText = afterText;
        StartLine = startLine;
        SafeCode = safeCode;
    }

    public string FileName { get; }

    public int Added { get; }

    public int Removed { get; }

    public string BeforeText { get; }

    public string AfterText { get; }

    public int? StartLine { get; }

    public string SafeCode { get; }

    public string AddedDeltaText => "+" + Added;

    public string RemovedDeltaText => "−" + Removed;

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpansionGlyph)));
        }
    }

    public string ExpansionGlyph => _isExpanded ? "\uE70D" : "\uE76C";

    public event PropertyChangedEventHandler? PropertyChanged;
}
```

- [ ] **Step 4: Implement the projection**

Create `gui-winui3/Services/ChangesTabProjection.cs`:

```csharp
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// The Changes tab's projection of the transcript's edit activities, plus the
/// session totals the card and the tab header share.
/// </summary>
/// <param name="Rows">Newest first, capped at <see cref="ChangesTabProjection.MaxRows"/>.</param>
/// <param name="Added">Added lines summed over the uncapped set.</param>
/// <param name="Removed">Removed lines summed over the uncapped set.</param>
public sealed record ChangesTabProjectionResult(
    IReadOnlyList<ChangesTabRow> Rows,
    int Added,
    int Removed);

/// <summary>
/// Builds the Changes tab from the feed's edit activities: newest first, capped
/// so a long session cannot grow the panel without bound. Pure and view-free so
/// the ordering, the cap, and the totals are testable.
/// </summary>
public static class ChangesTabProjection
{
    public const int MaxRows = 100;

    public static ChangesTabProjectionResult Project(IEnumerable<AgentActivityItem> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        var edits = new List<AgentActivityItem>();
        int added = 0;
        int removed = 0;
        foreach (var activity in activities)
        {
            if (activity.Kind != AgentActivityKind.Edit)
            {
                continue;
            }

            edits.Add(activity);
            added += activity.Added ?? 0;
            removed += activity.Removed ?? 0;
        }

        var rows = new List<ChangesTabRow>(Math.Min(edits.Count, MaxRows));
        for (var index = edits.Count - 1; index >= 0 && rows.Count < MaxRows; index--)
        {
            var activity = edits[index];
            rows.Add(new ChangesTabRow(
                activity.FileName ?? string.Empty,
                activity.Added ?? 0,
                activity.Removed ?? 0,
                activity.EditBeforeText,
                activity.EditAfterText,
                activity.LineStart,
                activity.SafeCode));
        }

        return new ChangesTabProjectionResult(rows, added, removed);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~ChangesTabProjectionTests"`
Expected: PASS — 5 passed, 0 failed.

- [ ] **Step 6: Commit**

```powershell
git add gui-winui3/Models/ChangesTabRow.cs gui-winui3/Services/ChangesTabProjection.cs gui-winui3.Tests/ChangesTabProjectionTests.cs
git commit -m "feat(gui): project session edits for the changes tab"
```

---

### Task 4: Title bar layout math

**Files:**
- Create: `gui-winui3/Services/TitleBarLayout.cs`
- Test: `gui-winui3.Tests/TitleBarLayoutTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `gui-winui3.Tests/TitleBarLayoutTests.cs`:

```csharp
using RuApp.Services;
using Windows.Foundation;
using Xunit;

namespace RuApp.Tests;

public sealed class TitleBarLayoutTests
{
    [Fact]
    public void The_inset_scales_from_physical_pixels_to_layout_units()
    {
        Assert.Equal(92, TitleBarLayout.InsetInLayoutUnits(138, 1.5), precision: 3);
    }

    [Fact]
    public void A_nonpositive_scale_falls_back_to_raw_pixels()
    {
        Assert.Equal(138, TitleBarLayout.InsetInLayoutUnits(138, 0), precision: 3);
        Assert.Equal(0, TitleBarLayout.InsetInLayoutUnits(-5, 1), precision: 3);
    }

    [Fact]
    public void Bounds_convert_to_physical_pixels()
    {
        var rect = TitleBarLayout.ToPhysicalRect(new Rect(10, 20, 30, 40), 1.5);

        Assert.Equal(15, rect.X);
        Assert.Equal(30, rect.Y);
        Assert.Equal(45, rect.Width);
        Assert.Equal(60, rect.Height);
    }

    [Fact]
    public void Negative_or_tiny_bounds_clamp_at_zero()
    {
        var rect = TitleBarLayout.ToPhysicalRect(new Rect(-4, -6, 0, 0), 1);

        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(0, rect.Width);
        Assert.Equal(0, rect.Height);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TitleBarLayoutTests"`
Expected: FAIL to compile — `TitleBarLayout` does not exist.

- [ ] **Step 3: Implement the helper**

Create `gui-winui3/Services/TitleBarLayout.cs`:

```csharp
using Windows.Foundation;
using Windows.Graphics;

namespace RuApp.Services;

/// <summary>
/// The arithmetic between the AppWindow title bar's physical-pixel insets and
/// the XAML title bar's device-independent layout: the caption-button inset in
/// layout units, and the passthrough region an interactive title-bar control
/// needs so the system forwards pointer input to it instead of the drag region.
/// </summary>
public static class TitleBarLayout
{
    public static double InsetInLayoutUnits(double physicalInset, double rasterizationScale)
    {
        double inset = Math.Max(0, physicalInset);
        return rasterizationScale > 0 ? inset / rasterizationScale : inset;
    }

    public static RectInt32 ToPhysicalRect(Rect bounds, double rasterizationScale)
    {
        double scale = rasterizationScale > 0 ? rasterizationScale : 1;
        return new RectInt32(
            (int)Math.Round(Math.Max(0, bounds.X) * scale),
            (int)Math.Round(Math.Max(0, bounds.Y) * scale),
            (int)Math.Round(Math.Max(0, bounds.Width) * scale),
            (int)Math.Round(Math.Max(0, bounds.Height) * scale));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TitleBarLayoutTests"`
Expected: PASS — 4 passed, 0 failed.

- [ ] **Step 5: Commit**

```powershell
git add gui-winui3/Services/TitleBarLayout.cs gui-winui3.Tests/TitleBarLayoutTests.cs
git commit -m "feat(gui): title bar layout math for the side panel toggle"
```

---

### Task 5: The side surface control (card + details panel)

**Files:**
- Create: `gui-winui3/Controls/AgentSidePanelControl.xaml`
- Create: `gui-winui3/Controls/AgentSidePanelControl.xaml.cs`

No unit test exists for XAML hosts; verification is the compile step here, and the source-contract tests in Task 6 pin the markup.

- [ ] **Step 1: Create the control markup**

Create `gui-winui3/Controls/AgentSidePanelControl.xaml`:

```xml
<UserControl
    x:Class="RuApp.Controls.AgentSidePanelControl"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:RuApp.Controls"
    xmlns:models="using:RuApp.Models"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d"
    HorizontalAlignment="Stretch"
    VerticalAlignment="Stretch">

    <!-- The right side surface in one host: the floating summary card and the
         full-height details panel. The root grid carries no background so the
         empty area stays hit-test transparent to the chat behind it. -->
    <Grid x:Name="SurfaceRoot">

        <Border x:Name="CardHost"
                Width="280"
                Margin="0,16,16,0"
                HorizontalAlignment="Right"
                VerticalAlignment="Top"
                Background="{StaticResource RuSurfaceBrush}"
                BorderBrush="{StaticResource RuLineBrush}"
                BorderThickness="1"
                CornerRadius="{StaticResource AppCardCornerRadius}"
                Padding="14,12"
                Visibility="Collapsed"
                AutomationProperties.Name="Session panel">
            <Border.RenderTransform>
                <TranslateTransform x:Name="CardSlide" />
            </Border.RenderTransform>
            <StackPanel Spacing="8">
                <Grid ColumnDefinitions="*,Auto">
                    <TextBlock x:Name="CardTitleText"
                               Text="Session"
                               FontFamily="{StaticResource RuUiFontFamily}"
                               FontSize="13"
                               FontWeight="SemiBold"
                               Foreground="{StaticResource RuInkBrush}"
                               TextTrimming="CharacterEllipsis"
                               VerticalAlignment="Center" />
                    <Button x:Name="ExpandButton"
                            Grid.Column="1"
                            Click="OnExpandClick"
                            Style="{StaticResource RuToolbarButtonStyle}"
                            CornerRadius="{StaticResource AppControlCornerRadius}"
                            ToolTipService.ToolTip="Open details panel"
                            AutomationProperties.Name="Open details panel">
                        <FontIcon Glyph="&#xE76C;" FontSize="12" Foreground="{StaticResource RuMutedBrush}" />
                    </Button>
                </Grid>

                <Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="8">
                    <FontIcon Glyph="&#xE70F;"
                              FontSize="12"
                              Foreground="{StaticResource RuMutedBrush}"
                              VerticalAlignment="Center" />
                    <TextBlock Grid.Column="1"
                               Text="Edits"
                               FontFamily="{StaticResource RuUiFontFamily}"
                               FontSize="12"
                               Foreground="{StaticResource RuMutedBrush}"
                               VerticalAlignment="Center" />
                    <StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="6">
                        <TextBlock x:Name="CardAddedText"
                                   Text="+0"
                                   FontFamily="{StaticResource RuMonoFontFamily}"
                                   FontSize="12"
                                   Foreground="{StaticResource RuSuccessBrush}"
                                   VerticalAlignment="Center" />
                        <TextBlock x:Name="CardRemovedText"
                                   Text="−0"
                                   FontFamily="{StaticResource RuMonoFontFamily}"
                                   FontSize="12"
                                   Foreground="{StaticResource RuDangerBrush}"
                                   VerticalAlignment="Center" />
                    </StackPanel>
                </Grid>

                <Border Height="1" Background="{StaticResource RuLineBrush}" />

                <TextBlock Text="TASKS"
                           FontFamily="{StaticResource RuUiFontFamily}"
                           FontSize="11"
                           FontWeight="Bold"
                           Foreground="{StaticResource RuMutedBrush}" />

                <TextBlock x:Name="CardTasksStatusText"
                           FontFamily="{StaticResource RuUiFontFamily}"
                           FontSize="11.5"
                           Foreground="{StaticResource RuMutedBrush}"
                           TextWrapping="Wrap" />

                <ScrollViewer MaxHeight="180"
                              HorizontalScrollBarVisibility="Disabled"
                              VerticalScrollBarVisibility="Auto">
                    <ItemsControl x:Name="CardTasksList" ItemsSource="{x:Bind Tasks, Mode=OneWay}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate x:DataType="models:AgentTaskRow">
                                <StackPanel Margin="0,0,0,6" Spacing="1">
                                    <TextBlock Text="{x:Bind Label}"
                                               FontFamily="{StaticResource RuMonoFontFamily}"
                                               FontSize="11.5"
                                               Foreground="{StaticResource RuInkBrush}"
                                               TextTrimming="CharacterEllipsis" />
                                    <TextBlock Text="{x:Bind DetailText}"
                                               FontFamily="{StaticResource RuUiFontFamily}"
                                               FontSize="11"
                                               Foreground="{StaticResource RuMutedBrush}" />
                                </StackPanel>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </ScrollViewer>
            </StackPanel>
        </Border>

        <Grid x:Name="PanelHost"
              Visibility="Collapsed"
              Background="{StaticResource RuCanvasBrush}"
              AutomationProperties.Name="Details panel">
            <Grid.RenderTransform>
                <TranslateTransform x:Name="PanelSlide" />
            </Grid.RenderTransform>

            <controls:SidebarResizerControl x:Name="PanelResizeGrip"
                                            Width="6"
                                            Margin="-3,0,0,0"
                                            HorizontalAlignment="Left"
                                            Background="Transparent"
                                            PointerEntered="OnGripPointerEntered"
                                            PointerExited="OnGripPointerExited"
                                            PointerPressed="OnGripPointerPressed"
                                            PointerMoved="OnGripPointerMoved"
                                            PointerReleased="OnGripPointerReleased" />

            <Border x:Name="PanelDivider"
                    Width="1"
                    HorizontalAlignment="Left"
                    Background="{StaticResource RuLineBrush}"
                    Opacity="0.6" />

            <Grid Margin="14,10,12,0" RowDefinitions="Auto,*">
                <Grid ColumnDefinitions="Auto,Auto,Auto,*,Auto,Auto" ColumnSpacing="2">
                    <Grid x:Name="ChangesChip" Grid.Column="0" ColumnDefinitions="Auto,Auto">
                        <Button x:Name="ChangesTabButton"
                                Click="OnChangesTabClick"
                                Style="{StaticResource RuToolbarButtonStyle}"
                                CornerRadius="{StaticResource AppControlCornerRadius}"
                                Padding="8,4"
                                ToolTipService.ToolTip="Changes"
                                AutomationProperties.Name="Changes tab">
                            <StackPanel Orientation="Horizontal" Spacing="6">
                                <FontIcon Glyph="&#xE70F;" FontSize="12" Foreground="{StaticResource RuMutedBrush}" />
                                <TextBlock x:Name="ChangesTabLabel"
                                           Text="Changes"
                                           FontFamily="{StaticResource RuUiFontFamily}"
                                           FontSize="12"
                                           Foreground="{StaticResource RuInkBrush}" />
                            </StackPanel>
                        </Button>
                        <Button x:Name="ChangesTabCloseButton"
                                Grid.Column="1"
                                Click="OnChangesTabCloseClick"
                                Style="{StaticResource RuToolbarButtonStyle}"
                                CornerRadius="{StaticResource AppControlCornerRadius}"
                                Width="22"
                                Height="22"
                                ToolTipService.ToolTip="Close Changes"
                                AutomationProperties.Name="Close Changes tab">
                            <FontIcon Glyph="&#xE711;" FontSize="10" Foreground="{StaticResource RuMutedBrush}" />
                        </Button>
                    </Grid>

                    <Grid x:Name="TasksChip" Grid.Column="1" ColumnDefinitions="Auto,Auto">
                        <Button x:Name="TasksTabButton"
                                Click="OnTasksTabClick"
                                Style="{StaticResource RuToolbarButtonStyle}"
                                CornerRadius="{StaticResource AppControlCornerRadius}"
                                Padding="8,4"
                                ToolTipService.ToolTip="Tasks"
                                AutomationProperties.Name="Tasks tab">
                            <StackPanel Orientation="Horizontal" Spacing="6">
                                <FontIcon Glyph="&#xE756;" FontSize="12" Foreground="{StaticResource RuMutedBrush}" />
                                <TextBlock x:Name="TasksTabLabel"
                                           Text="Tasks"
                                           FontFamily="{StaticResource RuUiFontFamily}"
                                           FontSize="12"
                                           Foreground="{StaticResource RuInkBrush}" />
                            </StackPanel>
                        </Button>
                        <Button x:Name="TasksTabCloseButton"
                                Grid.Column="1"
                                Click="OnTasksTabCloseClick"
                                Style="{StaticResource RuToolbarButtonStyle}"
                                CornerRadius="{StaticResource AppControlCornerRadius}"
                                Width="22"
                                Height="22"
                                ToolTipService.ToolTip="Close Tasks"
                                AutomationProperties.Name="Close Tasks tab">
                            <FontIcon Glyph="&#xE711;" FontSize="10" Foreground="{StaticResource RuMutedBrush}" />
                        </Button>
                    </Grid>

                    <Button x:Name="AddTabButton"
                            Grid.Column="2"
                            Click="OnAddTabClick"
                            Style="{StaticResource RuToolbarButtonStyle}"
                            CornerRadius="{StaticResource AppControlCornerRadius}"
                            Width="26"
                            Height="26"
                            ToolTipService.ToolTip="Add tab"
                            AutomationProperties.Name="Add tab">
                        <FontIcon Glyph="&#xE710;" FontSize="11" Foreground="{StaticResource RuMutedBrush}" />
                    </Button>

                    <Button x:Name="FullscreenButton"
                            Grid.Column="4"
                            Click="OnFullscreenClick"
                            Style="{StaticResource RuToolbarButtonStyle}"
                            CornerRadius="{StaticResource AppControlCornerRadius}"
                            Width="26"
                            Height="26"
                            ToolTipService.ToolTip="Fullscreen panel"
                            AutomationProperties.Name="Fullscreen panel">
                        <FontIcon x:Name="FullscreenIcon" Glyph="&#xE740;" FontSize="12" Foreground="{StaticResource RuMutedBrush}" />
                    </Button>

                    <Button x:Name="CollapseButton"
                            Grid.Column="5"
                            Click="OnCollapseClick"
                            Style="{StaticResource RuToolbarButtonStyle}"
                            CornerRadius="{StaticResource AppControlCornerRadius}"
                            Width="26"
                            Height="26"
                            ToolTipService.ToolTip="Collapse to summary"
                            AutomationProperties.Name="Collapse side panel">
                        <FontIcon Glyph="&#xE76B;" FontSize="12" Foreground="{StaticResource RuMutedBrush}" />
                    </Button>
                </Grid>

                <Grid Grid.Row="1">
                    <Grid x:Name="ChangesBody" Visibility="Collapsed" RowDefinitions="Auto,*" Margin="0,10,0,0">
                        <Grid ColumnDefinitions="*,Auto,Auto" ColumnSpacing="6">
                            <TextBlock Text="Changes"
                                       FontFamily="{StaticResource RuUiFontFamily}"
                                       FontSize="11"
                                       FontWeight="Bold"
                                       Foreground="{StaticResource RuMutedBrush}"
                                       VerticalAlignment="Center" />
                            <TextBlock x:Name="PanelAddedText"
                                       Grid.Column="1"
                                       Text="+0"
                                       FontFamily="{StaticResource RuMonoFontFamily}"
                                       FontSize="12"
                                       Foreground="{StaticResource RuSuccessBrush}" />
                            <TextBlock x:Name="PanelRemovedText"
                                       Grid.Column="2"
                                       Text="−0"
                                       FontFamily="{StaticResource RuMonoFontFamily}"
                                       FontSize="12"
                                       Foreground="{StaticResource RuDangerBrush}" />
                        </Grid>

                        <TextBlock x:Name="ChangesEmptyText"
                                   Grid.Row="1"
                                   Text="No changes yet."
                                   FontFamily="{StaticResource RuUiFontFamily}"
                                   FontSize="12"
                                   Foreground="{StaticResource RuMutedBrush}"
                                   TextWrapping="Wrap"
                                   Margin="0,6,0,0" />

                        <ScrollViewer Grid.Row="1"
                                      HorizontalScrollBarVisibility="Disabled"
                                      VerticalScrollBarVisibility="Auto">
                            <ItemsControl x:Name="ChangesList" ItemsSource="{x:Bind Changes, Mode=OneWay}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate x:DataType="models:ChangesTabRow">
                                        <StackPanel Margin="0,0,0,6">
                                            <Button Click="OnChangeRowToggleClick"
                                                    Tag="{x:Bind}"
                                                    Style="{StaticResource RuToolbarButtonStyle}"
                                                    HorizontalAlignment="Stretch"
                                                    HorizontalContentAlignment="Stretch"
                                                    CornerRadius="{StaticResource AppControlCornerRadius}"
                                                    Padding="6,3"
                                                    ToolTipService.ToolTip="Show the diff"
                                                    AutomationProperties.Name="Toggle change diff">
                                                <Grid ColumnDefinitions="Auto,*,Auto,Auto" ColumnSpacing="6">
                                                    <FontIcon Glyph="{x:Bind ExpansionGlyph, Mode=OneWay}"
                                                              FontSize="10"
                                                              Foreground="{StaticResource RuMutedBrush}"
                                                              VerticalAlignment="Center" />
                                                    <TextBlock Grid.Column="1"
                                                               Text="{x:Bind FileName}"
                                                               FontFamily="{StaticResource RuMonoFontFamily}"
                                                               FontSize="11.5"
                                                               Foreground="{StaticResource RuInkBrush}"
                                                               TextTrimming="CharacterEllipsis"
                                                               VerticalAlignment="Center" />
                                                    <TextBlock Grid.Column="2"
                                                               Text="{x:Bind AddedDeltaText}"
                                                               FontFamily="{StaticResource RuMonoFontFamily}"
                                                               FontSize="11.5"
                                                               Foreground="{StaticResource RuSuccessBrush}"
                                                               VerticalAlignment="Center" />
                                                    <TextBlock Grid.Column="3"
                                                               Text="{x:Bind RemovedDeltaText}"
                                                               FontFamily="{StaticResource RuMonoFontFamily}"
                                                               FontSize="11.5"
                                                               Foreground="{StaticResource RuDangerBrush}"
                                                               VerticalAlignment="Center" />
                                                </Grid>
                                            </Button>
                                            <controls:FileEditPreviewControl
                                                Margin="0,4,0,2"
                                                Visibility="{x:Bind IsExpanded, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}"
                                                BeforeText="{x:Bind BeforeText}"
                                                AfterText="{x:Bind AfterText}"
                                                StartLine="{x:Bind StartLine}" />
                                        </StackPanel>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                        </ScrollViewer>
                    </Grid>

                    <Grid x:Name="TasksBody" Visibility="Collapsed" RowDefinitions="Auto,*" Margin="0,10,0,0">
                        <TextBlock x:Name="PanelTasksStatusText"
                                   FontFamily="{StaticResource RuUiFontFamily}"
                                   FontSize="11.5"
                                   Foreground="{StaticResource RuMutedBrush}"
                                   TextWrapping="Wrap" />

                        <ScrollViewer Grid.Row="1"
                                      HorizontalScrollBarVisibility="Disabled"
                                      VerticalScrollBarVisibility="Auto">
                            <ItemsControl x:Name="AgentTasksList" ItemsSource="{x:Bind Tasks, Mode=OneWay}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate x:DataType="models:AgentTaskRow">
                                        <Grid ColumnSpacing="8" Margin="0,0,0,6">
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="*" />
                                                <ColumnDefinition Width="Auto" />
                                            </Grid.ColumnDefinitions>
                                            <StackPanel Grid.Column="0" Spacing="1">
                                                <TextBlock Text="{x:Bind Label}"
                                                           FontFamily="{StaticResource RuMonoFontFamily}"
                                                           FontSize="11.5"
                                                           Foreground="{StaticResource RuInkBrush}"
                                                           TextTrimming="CharacterEllipsis" />
                                                <TextBlock Text="{x:Bind DetailText}"
                                                           FontFamily="{StaticResource RuUiFontFamily}"
                                                           FontSize="11"
                                                           Foreground="{StaticResource RuMutedBrush}" />
                                            </StackPanel>
                                            <Button Grid.Column="1"
                                                    Content="Stop"
                                                    Tag="{x:Bind ExecutionId}"
                                                    Click="OnTaskStopClick"
                                                    Padding="8,3"
                                                    Visibility="{x:Bind StopVisibility}"
                                                    VerticalAlignment="Center"
                                                    AutomationProperties.Name="Stop background task" />
                                        </Grid>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                        </ScrollViewer>
                    </Grid>
                </Grid>
            </Grid>
        </Grid>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Create the control code-behind**

Create `gui-winui3/Controls/AgentSidePanelControl.xaml.cs`:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>
    /// The right side surface's hosts: the floating summary card and the
    /// full-height details panel. It renders what it is told and raises what the
    /// user did; which host shows and which tabs are open is decided by
    /// <see cref="AgentSidePanelPolicy"/> in the view.
    /// </summary>
    public sealed partial class AgentSidePanelControl : UserControl
    {
        public ObservableCollection<AgentTaskRow> Tasks { get; } = new();

        public ObservableCollection<ChangesTabRow> Changes { get; } = new();

        public event EventHandler? ExpandRequested;

        public event EventHandler? CollapseRequested;

        public event EventHandler? FullscreenToggleRequested;

        public event EventHandler<SidePanelTabKind>? TabActivated;

        public event EventHandler<SidePanelTabKind>? TabClosed;

        public event EventHandler<SidePanelTabKind>? TabAddRequested;

        public event EventHandler<string>? TaskStopRequested;

        public event EventHandler<double>? WidthChanged;

        public event EventHandler<double>? WidthCommitted;

        private AgentSidePanelMode _mode = AgentSidePanelMode.Hidden;
        private IReadOnlyList<SidePanelTabKind> _openTabs = Array.Empty<SidePanelTabKind>();
        private bool _isResizing;
        private double _resizeStartWidth;
        private double _resizeStartX;

        public AgentSidePanelControl()
        {
            InitializeComponent();
        }

        public void SetSessionTitle(string title) => CardTitleText.Text = title;

        public void SetTasksStatus(string status)
        {
            CardTasksStatusText.Text = status;
            PanelTasksStatusText.Text = status;
        }

        public void RenderChanges(IReadOnlyList<ChangesTabRow> rows, int added, int removed)
        {
            Changes.Clear();
            foreach (var row in rows)
            {
                Changes.Add(row);
            }

            CardAddedText.Text = "+" + added;
            CardRemovedText.Text = "−" + removed;
            PanelAddedText.Text = "+" + added;
            PanelRemovedText.Text = "−" + removed;
            ChangesEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ChangesList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public void RenderTasks(IReadOnlyList<AgentTaskRow> rows, string status)
        {
            Tasks.Clear();
            foreach (var row in rows)
            {
                Tasks.Add(row);
            }

            SetTasksStatus(status);
        }

        public void ApplyState(
            AgentSidePanelMode mode,
            IReadOnlyList<SidePanelTabKind> openTabs,
            SidePanelTabKind activeTab,
            bool fullscreen,
            double panelWidth)
        {
            var previous = _mode;
            _mode = mode;
            _openTabs = openTabs;

            bool showCard = mode == AgentSidePanelMode.Card;
            bool showPanel = mode == AgentSidePanelMode.Panel;
            CardHost.Visibility = showCard ? Visibility.Visible : Visibility.Collapsed;
            PanelHost.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;

            ApplyChip(ChangesChip, ChangesTabButton, ChangesTabLabel, openTabs.Contains(SidePanelTabKind.Changes), activeTab == SidePanelTabKind.Changes);
            ApplyChip(TasksChip, TasksTabButton, TasksTabLabel, openTabs.Contains(SidePanelTabKind.Tasks), activeTab == SidePanelTabKind.Tasks);
            AddTabButton.Visibility = openTabs.Count < 2 ? Visibility.Visible : Visibility.Collapsed;

            ChangesBody.Visibility = showPanel && activeTab == SidePanelTabKind.Changes ? Visibility.Visible : Visibility.Collapsed;
            TasksBody.Visibility = showPanel && activeTab == SidePanelTabKind.Tasks ? Visibility.Visible : Visibility.Collapsed;

            bool push = showPanel && !fullscreen;
            PanelHost.HorizontalAlignment = push ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            if (push)
            {
                PanelHost.Width = AgentSidePanelPolicy.ClampWidth(panelWidth);
            }
            else
            {
                PanelHost.ClearValue(FrameworkElement.WidthProperty);
            }

            PanelResizeGrip.Visibility = push ? Visibility.Visible : Visibility.Collapsed;
            PanelDivider.Visibility = push ? Visibility.Visible : Visibility.Collapsed;

            FullscreenIcon.Glyph = fullscreen ? "\uE73F" : "\uE740";
            string fullscreenName = fullscreen ? "Exit fullscreen" : "Fullscreen panel";
            ToolTipService.SetToolTip(FullscreenButton, fullscreenName);
            AutomationProperties.SetName(FullscreenButton, fullscreenName);

            if (mode != previous && mode != AgentSidePanelMode.Hidden)
            {
                PlayEntrance(showPanel ? PanelHost : CardHost, showPanel ? PanelSlide : CardSlide);
            }
        }

        private static void ApplyChip(Grid chip, Button label, TextBlock labelText, bool isOpen, bool isActive)
        {
            chip.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            label.Background = isActive
                ? (Brush)Application.Current.Resources["RuSurfaceBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            labelText.Foreground = (Brush)Application.Current.Resources[isActive ? "RuInkBrush" : "RuMutedBrush"];
            labelText.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private static void PlayEntrance(FrameworkElement host, TranslateTransform slide)
        {
            if (!UiTiming.AnimationsEnabled)
            {
                host.Opacity = 1;
                slide.X = 0;
                return;
            }

            host.Opacity = 0;
            slide.X = 16;

            var fade = new DoubleAnimation
            {
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, host);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var glide = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(glide, slide);
            Storyboard.SetTargetProperty(glide, "X");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Children.Add(glide);
            storyboard.Begin();
        }

        private void OnExpandClick(object sender, RoutedEventArgs e) =>
            ExpandRequested?.Invoke(this, EventArgs.Empty);

        private void OnCollapseClick(object sender, RoutedEventArgs e) =>
            CollapseRequested?.Invoke(this, EventArgs.Empty);

        private void OnFullscreenClick(object sender, RoutedEventArgs e) =>
            FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);

        private void OnChangesTabClick(object sender, RoutedEventArgs e) =>
            TabActivated?.Invoke(this, SidePanelTabKind.Changes);

        private void OnTasksTabClick(object sender, RoutedEventArgs e) =>
            TabActivated?.Invoke(this, SidePanelTabKind.Tasks);

        private void OnChangesTabCloseClick(object sender, RoutedEventArgs e) =>
            TabClosed?.Invoke(this, SidePanelTabKind.Changes);

        private void OnTasksTabCloseClick(object sender, RoutedEventArgs e) =>
            TabClosed?.Invoke(this, SidePanelTabKind.Tasks);

        private void OnAddTabClick(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();
            foreach (var kind in new[] { SidePanelTabKind.Changes, SidePanelTabKind.Tasks })
            {
                if (_openTabs.Contains(kind))
                {
                    continue;
                }

                var item = new MenuFlyoutItem
                {
                    Text = kind == SidePanelTabKind.Changes ? "Changes" : "Tasks",
                };
                item.Click += (_, _) => TabAddRequested?.Invoke(this, kind);
                menu.Items.Add(item);
            }

            if (menu.Items.Count > 0)
            {
                menu.ShowAt(AddTabButton);
            }
        }

        private void OnChangeRowToggleClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ChangesTabRow row })
            {
                row.IsExpanded = !row.IsExpanded;
            }
        }

        private void OnTaskStopClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string executionId })
            {
                TaskStopRequested?.Invoke(this, executionId);
            }
        }

        private void OnGripPointerEntered(object sender, PointerRoutedEventArgs e) =>
            PanelDivider.Opacity = 0.9;

        private void OnGripPointerExited(object sender, PointerRoutedEventArgs e) =>
            PanelDivider.Opacity = 0.6;

        private void OnGripPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (PanelHost.Visibility != Visibility.Visible
                || PanelHost.HorizontalAlignment != HorizontalAlignment.Right)
            {
                return;
            }

            _isResizing = true;
            _resizeStartWidth = PanelHost.Width;
            _resizeStartX = e.GetCurrentPoint(this).Position.X;
            PanelResizeGrip.CapturePointer(e.Pointer);
        }

        private void OnGripPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizing)
            {
                return;
            }

            double currentX = e.GetCurrentPoint(this).Position.X;
            double width = AgentSidePanelPolicy.ClampWidth(_resizeStartWidth + (_resizeStartX - currentX));
            PanelHost.Width = width;
            WidthChanged?.Invoke(this, width);
        }

        private void OnGripPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isResizing)
            {
                return;
            }

            _isResizing = false;
            PanelResizeGrip.ReleasePointerCapture(e.Pointer);
            WidthCommitted?.Invoke(this, PanelHost.Width);
        }
    }
}
```

- [ ] **Step 3: Build to verify the control compiles**

Run: `dotnet build gui-winui3/RuApp.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors.
If `{x:Bind}` in the `Tag` or the `RelativeSource` bindings complain, fix them to the working form (existing markup uses `Tag="{x:Bind}"` in `ChatView.xaml`, so it is known-supported; do not change the pinned names).

- [ ] **Step 4: Commit**

```powershell
git add gui-winui3/Controls/AgentSidePanelControl.xaml gui-winui3/Controls/AgentSidePanelControl.xaml.cs
git commit -m "feat(gui): side surface control with card and details panel"
```

---

### Task 6: Wire the side surface into the chat view

**Files:**
- Create: `gui-winui3/Views/ChatView.SidePanel.cs`
- Modify: `gui-winui3/Views/ChatView.xaml` (replace the old card + show button with the control)
- Modify: `gui-winui3/Views/ChatView.xaml.cs` (remove replaced fields/methods, call `InitializeSidePanel`, simplify `UpdateViewMode`)
- Modify: `gui-winui3/Views/ChatView.Tasks.cs` (feed the control; remove the old handlers)
- Test: `gui-winui3.Tests/AgentTasksUiContractTests.cs` (update)
- Test: `gui-winui3.Tests/AgentSidePanelUiContractTests.cs` (create)

- [ ] **Step 1: Update the contract tests first (they must fail against the old sources)**

Replace `gui-winui3.Tests/AgentTasksUiContractTests.cs` with:

```csharp
using System;
using Microsoft.UI.Xaml;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The side surface's background-task section: rows come from the Host's /tasks
/// command and a stop is the same command with a kill argument, so the Broker
/// stays the only execution authority and the view never talks to it directly.
/// The composer flyout was replaced by the surface by owner request.
/// </summary>
public sealed class AgentTasksUiContractTests
{
    [Fact]
    public void Tasks_panel_lists_and_stops_through_the_command_route()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        GuiSource.Require(document, "AgentTasksList");
        Assert.DoesNotContain("AgentTasksButton", GuiSource.XamlNames(document));

        var code = GuiSource.ViewText("ChatView");
        Assert.Contains("\"/tasks\"", code, StringComparison.Ordinal);
        Assert.Contains("\"/tasks kill \"", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_surface_only_shows_with_chat_content_in_a_wide_enough_window()
    {
        // The surface reserves its own space and hides when the chat area is
        // narrow or the chat is empty, so it can never cover the conversation.
        var code = GuiSource.ViewText("ChatView");
        string canShow = GuiSource.MethodBody(code, "CanShowSidePanel");
        Assert.Contains("Messages.Count > 0", canShow, StringComparison.Ordinal);
        Assert.Contains("SidePanelMinRootWidth", canShow, StringComparison.Ordinal);

        string layout = GuiSource.MethodBody(code, "UpdateSidePanelLayout");
        Assert.Contains("AgentSidePanelMode.Hidden", layout, StringComparison.Ordinal);
        Assert.Contains("_sidePanelReserveWidth", layout, StringComparison.Ordinal);

        string reserve = GuiSource.MethodBody(code, "ReserveFor");
        Assert.Contains("EffectiveSidePanelReserve", reserve, StringComparison.Ordinal);

        Assert.Contains(
            "_sidePanelReserveWidth",
            GuiSource.MethodBody(code, "ApplyInputWidth"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Task_rows_only_offer_stop_while_running()
    {
        var running = new AgentTaskRow("req-1", "running", null);
        var done = new AgentTaskRow("req-2", "exited", 0);

        Assert.Equal(Visibility.Visible, running.StopVisibility);
        Assert.Equal(Visibility.Collapsed, done.StopVisibility);
        Assert.Equal("exit 0", done.DetailText);
        Assert.Contains("req-1", running.Label, StringComparison.Ordinal);
    }
}
```

Create `gui-winui3.Tests/AgentSidePanelUiContractTests.cs`:

```csharp
using System;
using System.Linq;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The right side surface's layout contract: the card stays the summary, the
/// details panel carries the tabs, and the title bar carries the one control
/// that hides and restores the whole surface.
/// </summary>
public sealed class AgentSidePanelUiContractTests
{
    [Fact]
    public void The_surface_has_a_summary_card_and_a_details_panel()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        foreach (var name in new[]
        {
            "CardHost", "PanelHost", "PanelDivider", "PanelResizeGrip",
            "ChangesChip", "TasksChip", "AddTabButton", "FullscreenButton", "CollapseButton",
            "ChangesBody", "TasksBody", "ChangesList", "AgentTasksList", "ChangesEmptyText",
        })
        {
            GuiSource.Require(document, name);
        }
    }

    [Fact]
    public void The_summary_card_lists_tasks_without_the_stop_control()
    {
        var document = GuiSource.Xml("Controls", "AgentSidePanelControl.xaml");
        var named = GuiSource.NamedElements(document);

        var cardList = named["CardTasksList"];
        Assert.Empty(cardList.Descendants()
            .Where(element => GuiSource.AttributeValue(element, "Click") == "OnTaskStopClick"));

        var panelList = named["AgentTasksList"];
        Assert.NotEmpty(panelList.Descendants()
            .Where(element => GuiSource.AttributeValue(element, "Click") == "OnTaskStopClick"));
    }

    [Fact]
    public void The_details_panel_closes_tabs_and_collapses_back_to_the_card()
    {
        var code = GuiSource.ViewText("ChatView");
        Assert.Contains("CloseTab", GuiSource.MethodBody(code, "OnSidePanelTabClosed"), StringComparison.Ordinal);
        Assert.Contains("CollapseToCard", GuiSource.MethodBody(code, "OnSidePanelCollapseRequested"), StringComparison.Ordinal);
        Assert.Contains("ToggleVisibility", GuiSource.MethodBody(code, "ToggleSidePanel"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_view_answers_the_toggle_state()
    {
        var code = GuiSource.ViewText("ChatView");
        Assert.Contains(
            "UpdateSidePanelToggleState",
            GuiSource.MethodBody(code, "UpdateSidePanelLayout"),
            StringComparison.Ordinal);
    }
}
```

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AgentTasksUiContractTests|FullyQualifiedName~AgentSidePanelUiContractTests"`
Expected: FAIL — the control file assertions and the new ChatView members do not exist yet.

- [ ] **Step 2: Create the view logic partial**

Create `gui-winui3/Views/ChatView.SidePanel.cs`:

```csharp
using System;
using Microsoft.UI.Xaml;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The right side surface's view logic: which host is showing for the active
/// session, how much width the transcript and composer leave for it, the
/// persisted width, and the events the panel control raises. The decisions live
/// in <see cref="AgentSidePanelPolicy"/>; the control only renders. The one
/// exception is the title-bar toggle's state, which MainWindow draws.
/// </summary>
public sealed partial class ChatView
{
    private const double SidePanelMargin = 16;
    private const double SidePanelCardWidth = 280;

    /// <summary>
    /// The side surface only shows when the chat area is at least this wide, so
    /// the transcript, composer and approval card keep a comfortable column
    /// after the surface reserves its space.
    /// </summary>
    private const double SidePanelMinRootWidth = 1120;

    private readonly AgentSidePanelPolicy _sidePanelPolicy = new();
    private readonly SidePanelPreferenceStore _sidePanelWidthStore = new();
    private double _sidePanelWidth;
    private double _sidePanelReserveWidth;

    private void InitializeSidePanel()
    {
        _sidePanelWidth = _sidePanelWidthStore.Load();
        SidePanel.ExpandRequested += OnSidePanelExpandRequested;
        SidePanel.CollapseRequested += OnSidePanelCollapseRequested;
        SidePanel.FullscreenToggleRequested += OnSidePanelFullscreenToggleRequested;
        SidePanel.TabActivated += OnSidePanelTabActivated;
        SidePanel.TabClosed += OnSidePanelTabClosed;
        SidePanel.TabAddRequested += OnSidePanelTabAddRequested;
        SidePanel.TaskStopRequested += OnSidePanelTaskStopRequested;
        SidePanel.WidthChanged += OnSidePanelWidthChanged;
        SidePanel.WidthCommitted += OnSidePanelWidthCommitted;
    }

    /// <summary>Hides or restores the whole surface; the title-bar toggle's action.</summary>
    public void ToggleSidePanel()
    {
        if (!CanShowSidePanel())
        {
            return;
        }

        _sidePanelPolicy.ToggleVisibility(_activeSessionId);
        ApplySidePanelState();
    }

    /// <summary>Drops a deleted session's surface state.</summary>
    public void ForgetSidePanelState(string sessionId) => _sidePanelPolicy.Remove(sessionId);

    private bool CanShowSidePanel() =>
        RootGrid is not null && Messages.Count > 0 && RootGrid.ActualWidth >= SidePanelMinRootWidth;

    private void ApplySidePanelState()
    {
        double previousReserve = _sidePanelReserveWidth;
        UpdateSidePanelLayout();
        if (Math.Abs(previousReserve - _sidePanelReserveWidth) > 0.5)
        {
            ApplyInputWidth();
        }
    }

    private void UpdateSidePanelLayout()
    {
        if (SidePanel is null || RootGrid is null)
        {
            return;
        }

        bool canShow = CanShowSidePanel();
        var mode = _sidePanelPolicy.GetMode(_activeSessionId);
        if (!canShow && mode == AgentSidePanelMode.Panel)
        {
            _sidePanelPolicy.CollapseToCard(_activeSessionId);
            mode = _sidePanelPolicy.GetMode(_activeSessionId);
        }

        bool isVisible = canShow && mode != AgentSidePanelMode.Hidden;
        SidePanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        if (isVisible)
        {
            SidePanel.ApplyState(
                mode,
                _sidePanelPolicy.GetOpenTabs(_activeSessionId),
                _sidePanelPolicy.GetActiveTab(_activeSessionId),
                _sidePanelPolicy.IsFullscreen(_activeSessionId),
                _sidePanelWidth);
        }

        _sidePanelReserveWidth = !isVisible ? 0 : ReserveFor(mode);
        if (ApprovalHostGrid is not null)
        {
            ApprovalHostGrid.Margin = new Thickness(16, 0, 16 + _sidePanelReserveWidth, 24);
        }

        MainWindow.Instance?.UpdateSidePanelToggleState(canShow, mode == AgentSidePanelMode.Hidden);
    }

    private double ReserveFor(AgentSidePanelMode mode) =>
        mode switch
        {
            AgentSidePanelMode.Card => EffectiveSidePanelReserve(SidePanelCardWidth),
            AgentSidePanelMode.Panel when _sidePanelPolicy.IsFullscreen(_activeSessionId) => 0,
            AgentSidePanelMode.Panel => EffectiveSidePanelReserve(_sidePanelWidth),
            _ => 0,
        };

    /// <summary>
    /// How much width the surface needs to reserve: only the shift the centered
    /// column needs to clear the host (plus a breathing gap), so a wide window
    /// keeps the chat column centered instead of pushing it off to one side.
    /// </summary>
    private double EffectiveSidePanelReserve(double hostWidth)
    {
        if (RootGrid is null)
        {
            return 0;
        }

        double panelReserve = hostWidth + SidePanelMargin;
        double fullColumn = Math.Min(
            ReadingColumnMaxWidth,
            RootGrid.ActualWidth - HorizontalInsetFor(RootGrid.ActualWidth));
        double columnRight = (RootGrid.ActualWidth + fullColumn) / 2;
        double panelLeft = RootGrid.ActualWidth - panelReserve;
        double overlap = columnRight + 16 - panelLeft;
        return overlap <= 0 ? 0 : Math.Min(panelReserve, overlap * 2);
    }

    private void OnSidePanelExpandRequested(object? sender, EventArgs e)
    {
        _sidePanelPolicy.ExpandToPanel(_activeSessionId);
        ApplySidePanelState();
        _ = RefreshAgentTasksAsync();
    }

    private void OnSidePanelCollapseRequested(object? sender, EventArgs e)
    {
        _sidePanelPolicy.CollapseToCard(_activeSessionId);
        ApplySidePanelState();
    }

    private void OnSidePanelFullscreenToggleRequested(object? sender, EventArgs e)
    {
        _sidePanelPolicy.ToggleFullscreen(_activeSessionId);
        ApplySidePanelState();
    }

    private void OnSidePanelTabActivated(object? sender, SidePanelTabKind kind)
    {
        _sidePanelPolicy.ActivateTab(_activeSessionId, kind);
        ApplySidePanelState();
        if (kind == SidePanelTabKind.Tasks)
        {
            _ = RefreshAgentTasksAsync();
        }
    }

    private void OnSidePanelTabClosed(object? sender, SidePanelTabKind kind)
    {
        _sidePanelPolicy.CloseTab(_activeSessionId, kind);
        ApplySidePanelState();
    }

    private void OnSidePanelTabAddRequested(object? sender, SidePanelTabKind kind)
    {
        _sidePanelPolicy.OpenTab(_activeSessionId, kind);
        ApplySidePanelState();
        if (kind == SidePanelTabKind.Tasks)
        {
            _ = RefreshAgentTasksAsync();
        }
    }

    private async void OnSidePanelTaskStopRequested(object? sender, string executionId)
    {
        if (_agentController is null)
        {
            return;
        }

        var runId = _agentController.CurrentRun?.RunId ?? _activeAgentRunId;
        if (string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        try
        {
            await _agentController
                .DispatchCommandAsync(runId, "/tasks kill " + executionId)
                .ConfigureAwait(true);
        }
        catch (Exception)
        {
            SidePanel.SetTasksStatus("Stop failed; the host did not answer.");
            return;
        }

        await RefreshAgentTasksAsync();
    }

    private void OnSidePanelWidthChanged(object? sender, double width)
    {
        _sidePanelWidth = width;
        ApplySidePanelState();
    }

    private void OnSidePanelWidthCommitted(object? sender, double width)
    {
        _sidePanelWidth = width;
        _sidePanelWidthStore.TrySave(width);
    }
}
```

- [ ] **Step 3: Replace the old card markup in ChatView.xaml**

In `gui-winui3/Views/ChatView.xaml`, delete the whole block from the comment starting `<!-- Right side panel (Codex-style): the session's running edit totals and` through the `SidePanelOpenButton` button element (the block currently spanning roughly lines 716–852), and insert in its place:

```xml
        <!-- The right side surface: the floating summary card and the full-height
             details panel live in one control; the view answers which host is
             showing through AgentSidePanelPolicy. -->
        <controls:AgentSidePanelControl x:Name="SidePanel"
                                        Grid.Row="0"
                                        Grid.RowSpan="2" />
```

Ensure no other markup still references `SidePanelCard`, `SidePanelOpenButton`, `SidePanelTitleText`, `SidePanelAddedText`, `SidePanelRemovedText`, `SidePanelSlide`, `SidePanelCollapseButton`, `AgentTasksList`, or `AgentTasksStatusText`.

- [ ] **Step 4: Rewrite ChatView.Tasks.cs**

Replace the whole of `gui-winui3/Views/ChatView.Tasks.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The right side surface's background-task and edit data. Rows come from the
/// Host's typed <c>/tasks</c> command and a stop is the same command with a kill
/// argument, so the Broker stays the only execution authority and the view
/// never talks to it directly. The summary card and the details panel render
/// the same rows; only the panel offers Stop.
/// </summary>
public sealed partial class ChatView
{
    private const int MaxRenderedAgentTasks = 16;

    private bool? _sidePanelRunRunning;

    /// <summary>
    /// Keeps the surface fresh from run-state updates: the title and edit
    /// projections are cheap, and the task list refreshes only when the run
    /// starts or settles so the panel never polls while streaming.
    /// </summary>
    private void UpdateSidePanel(bool isRunRunning)
    {
        if (SidePanel is null)
        {
            return;
        }

        SidePanel.SetSessionTitle(_activeSessionTitle);
        RefreshSidePanelEdits();

        if (_sidePanelRunRunning != isRunRunning)
        {
            _sidePanelRunRunning = isRunRunning;
            _ = RefreshAgentTasksAsync();
        }
    }

    /// <summary>Rebuilds the Changes tab rows and the shared +added −removed totals.</summary>
    private void RefreshSidePanelEdits()
    {
        if (SidePanel is null)
        {
            return;
        }

        var projection = ChangesTabProjection.Project(AgentActivities);
        SidePanel.RenderChanges(projection.Rows, projection.Added, projection.Removed);
    }

    private async Task RefreshAgentTasksAsync()
    {
        if (SidePanel is null)
        {
            return;
        }

        var runId = _agentController?.CurrentRun?.RunId ?? _activeAgentRunId;
        if (string.IsNullOrWhiteSpace(runId) || _agentController is null)
        {
            SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), "No active run in this session.");
            return;
        }

        try
        {
            var response = await _agentController
                .DispatchCommandAsync(runId, "/tasks")
                .ConfigureAwait(true);
            if (!string.Equals(response.Status, "succeeded", StringComparison.Ordinal))
            {
                SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), $"Tasks unavailable ({response.Code}).");
                return;
            }

            RenderAgentTasks(response.Data);
        }
        catch (Exception)
        {
            SidePanel.RenderTasks(Array.Empty<AgentTaskRow>(), "Tasks unavailable; the host did not answer.");
        }
    }

    private void RenderAgentTasks(object? data)
    {
        var rows = ParseAgentTasks(data);
        SidePanel.RenderTasks(
            rows,
            rows.Count == 0 ? "No background tasks for this session." : $"{rows.Count} task(s).");
    }

    private static List<AgentTaskRow> ParseAgentTasks(object? data)
    {
        var rows = new List<AgentTaskRow>();
        if (data is not JsonElement element
            || element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("tasks", out var tasks)
            || tasks.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var task in tasks.EnumerateArray())
        {
            if (rows.Count >= MaxRenderedAgentTasks)
            {
                break;
            }

            if (task.ValueKind != JsonValueKind.Object
                || !task.TryGetProperty("execution_id", out var idProperty)
                || idProperty.GetString() is not { Length: > 0 } executionId)
            {
                continue;
            }

            var state = task.TryGetProperty("state", out var stateProperty)
                ? stateProperty.GetString() ?? "unknown"
                : "unknown";
            int? exitCode = task.TryGetProperty("exit_code", out var exitProperty)
                && exitProperty.ValueKind == JsonValueKind.Number
                && exitProperty.TryGetInt32(out var parsedExit)
                    ? parsedExit
                    : null;
            rows.Add(new AgentTaskRow(executionId, state, exitCode));
        }

        return rows;
    }
}
```

- [ ] **Step 5: Trim ChatView.xaml.cs and call the initializer**

In `gui-winui3/Views/ChatView.xaml.cs`:

1. Delete the constants `SidePanelMargin` and the `SidePanelMinRootWidth` doc comment + constant (they moved to `ChatView.SidePanel.cs`).
2. Delete the fields `_sidePanelUserCollapsed` and `_sidePanelReserveWidth` (they moved).
3. Delete the methods `UpdateSidePanelLayout`, `EffectiveSidePanelReserve`, and `PlaySidePanelEntrance` (replaced by the new partial/control).
4. In `UpdateViewMode`, replace:

```csharp
            double previousReserve = _sidePanelReserveWidth;
            UpdateSidePanelLayout();
            if (Math.Abs(previousReserve - _sidePanelReserveWidth) > 0.5)
            {
                ApplyInputWidth();
            }
```

with:

```csharp
            ApplySidePanelState();
```

5. In the constructor, between `InitializeChatHoverEffects();` and `UpdateViewMode();`, add:

```csharp
            InitializeSidePanel();
```

- [ ] **Step 6: Build and run the contract tests**

Run: `dotnet build gui-winui3/RuApp.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AgentTasksUiContractTests|FullyQualifiedName~AgentSidePanelUiContractTests"`
Expected: PASS.

If other GUI source-contract tests fail on removed names (`SidePanelCard`, `_sidePanelUserCollapsed`, etc.), grep the test project for those names, and update only the assertions whose pinned decision intentionally changed here.

- [ ] **Step 7: Commit**

```powershell
git add gui-winui3/Views/ChatView.SidePanel.cs gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3/Views/ChatView.Tasks.cs gui-winui3.Tests/AgentTasksUiContractTests.cs gui-winui3.Tests/AgentSidePanelUiContractTests.cs
git commit -m "feat(gui): wire the side surface into the chat view"
```

---

### Task 7: The title-bar toggle

**Files:**
- Modify: `gui-winui3/MainWindow.xaml` (button + style inside `AppTitleBar`)
- Create: `gui-winui3/MainWindow.SidePanel.cs`
- Modify: `gui-winui3/MainWindow.xaml.cs` (call `InitializeSidePanelToggle`)
- Modify: `gui-winui3/MainWindow.SessionActions.cs` (drop a deleted session's surface state)
- Test: `gui-winui3.Tests/AgentSidePanelUiContractTests.cs` (extend)

- [ ] **Step 1: Extend the contract test (it must fail first)**

Append to `gui-winui3.Tests/AgentSidePanelUiContractTests.cs`, before the closing brace:

```csharp
    [Fact]
    public void The_title_bar_toggle_sits_beside_the_caption_buttons_and_forwards_input()
    {
        var document = GuiSource.Xml("MainWindow.xaml");
        GuiSource.Require(document, "SidePanelToggleButton");

        var code = GuiSource.ViewText("MainWindow");
        string layout = GuiSource.MethodBody(code, "UpdateTitleBarToggleLayout");
        Assert.Contains("RightInset", layout, StringComparison.Ordinal);
        Assert.Contains("RasterizationScale", layout, StringComparison.Ordinal);

        string region = GuiSource.MethodBody(code, "ApplySidePanelToggleRegion");
        Assert.Contains("SetRegionRects", region, StringComparison.Ordinal);

        Assert.Contains(
            "ToggleSidePanel",
            GuiSource.MethodBody(code, "OnSidePanelToggleClick"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ForgetSidePanelState",
            GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "ForgetSidePanelState"),
            StringComparison.Ordinal);
    }
```

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AgentSidePanelUiContractTests"`
Expected: FAIL — `SidePanelToggleButton` does not exist.

- [ ] **Step 2: Add the button and its style to the title bar**

In `gui-winui3/MainWindow.xaml`, replace:

```xml
        <!-- Custom App TitleBar (Clean draggable titlebar) -->
        <Grid x:Name="AppTitleBar" Grid.Row="0" Height="40" Background="Transparent"/>
```

with:

```xml
        <!-- Custom App TitleBar (Clean draggable titlebar) -->
        <Grid x:Name="AppTitleBar" Grid.Row="0" Height="40" Background="Transparent">
            <Grid.Resources>
                <Style x:Key="TitleBarIconButtonStyle" TargetType="Button">
                    <Setter Property="Background" Value="Transparent" />
                    <Setter Property="BorderThickness" Value="0" />
                    <Setter Property="Width" Value="36" />
                    <Setter Property="Height" Value="32" />
                    <Setter Property="Padding" Value="0" />
                    <Setter Property="UseSystemFocusVisuals" Value="True" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="Button">
                                <Grid x:Name="Root" Background="{TemplateBinding Background}">
                                    <VisualStateManager.VisualStateGroups>
                                        <VisualStateGroup x:Name="CommonStates">
                                            <VisualState x:Name="Normal" />
                                            <VisualState x:Name="PointerOver">
                                                <VisualState.Setters>
                                                    <Setter Target="Root.Background" Value="{StaticResource RuAccentSoftBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                            <VisualState x:Name="Pressed">
                                                <VisualState.Setters>
                                                    <Setter Target="Root.Background" Value="{StaticResource RuLineBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                            <VisualState x:Name="Disabled">
                                                <VisualState.Setters>
                                                    <Setter Target="Root.Opacity" Value="0.4" />
                                                </VisualState.Setters>
                                            </VisualState>
                                        </VisualStateGroup>
                                    </VisualStateManager.VisualStateGroups>
                                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                                </Grid>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </Grid.Resources>

            <!-- Hides and restores the whole right side surface. Its right margin
                 is the caption buttons' inset, set in code, so it sits immediately
                 left of Minimize. -->
            <Button x:Name="SidePanelToggleButton"
                    HorizontalAlignment="Right"
                    VerticalAlignment="Center"
                    IsEnabled="False"
                    Click="OnSidePanelToggleClick"
                    Style="{StaticResource TitleBarIconButtonStyle}"
                    ToolTipService.ToolTip="Show side panel"
                    AutomationProperties.Name="Show side panel">
                <Viewbox Width="16" Height="16">
                    <Path Data="M4.5,3.5 H11.5 A2,2 0 0 1 13.5,5.5 V10.5 A2,2 0 0 1 11.5,12.5 H4.5 A2,2 0 0 1 2.5,10.5 V5.5 A2,2 0 0 1 4.5,3.5 Z M10.5,3.5 V12.5"
                          Stroke="{StaticResource RuInkBrush}"
                          StrokeThickness="1.2"
                          StrokeLineJoin="Round"
                          StrokeStartLineCap="Round"
                          StrokeEndLineCap="Round" />
                </Viewbox>
            </Button>
        </Grid>
```

- [ ] **Step 3: Implement the toggle wiring**

Create `gui-winui3/MainWindow.SidePanel.cs`:

```csharp
using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp
{
    /// <summary>
    /// The title bar's side-surface toggle: the one control that hides and
    /// restores the whole right side surface, parked immediately left of the
    /// system caption buttons. Interactive title-bar content needs explicit
    /// passthrough regions, so the button's rectangle is registered with the
    /// window's non-client input source and refreshed whenever the bar's layout
    /// changes.
    /// </summary>
    public sealed partial class MainWindow
    {
        private void InitializeSidePanelToggle()
        {
            if (AppTitleBar is null || SidePanelToggleButton is null)
            {
                return;
            }

            AppTitleBar.Loaded += (_, _) => UpdateTitleBarToggleLayout();
            AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarToggleLayout();
            Activated += OnWindowActivatedForSidePanelToggle;
        }

        private void UpdateTitleBarToggleLayout()
        {
            if (_appWindow is null
                || AppTitleBar?.XamlRoot is not { } xamlRoot
                || SidePanelToggleButton is null)
            {
                return;
            }

            double scale = xamlRoot.RasterizationScale;
            SidePanelToggleButton.Margin = new Thickness(
                0,
                0,
                TitleBarLayout.InsetInLayoutUnits(_appWindow.TitleBar.RightInset, scale),
                0);
            ApplySidePanelToggleRegion(scale);
        }

        private void ApplySidePanelToggleRegion(double scale)
        {
            if (_appWindow is null
                || SidePanelToggleButton is null
                || SidePanelToggleButton.ActualWidth <= 0)
            {
                return;
            }

            var bounds = SidePanelToggleButton.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(
                    0,
                    0,
                    SidePanelToggleButton.ActualWidth,
                    SidePanelToggleButton.ActualHeight));
            var source = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            source.SetRegionRects(
                NonClientRegionKind.Passthrough,
                new[] { TitleBarLayout.ToPhysicalRect(bounds, scale) });
        }

        private void OnWindowActivatedForSidePanelToggle(object sender, WindowActivatedEventArgs args)
        {
            if (SidePanelToggleButton is null)
            {
                return;
            }

            bool isActive = args.WindowActivationState != WindowActivationState.Deactivated;
            SidePanelToggleButton.Opacity = isActive ? 1 : 0.55;
        }

        /// <summary>
        /// Keeps the toggle honest about what it can do: enabled only while the
        /// surface can show, and worded for the action it will take.
        /// </summary>
        public void UpdateSidePanelToggleState(bool canToggle, bool isHidden)
        {
            if (SidePanelToggleButton is null)
            {
                return;
            }

            SidePanelToggleButton.IsEnabled = canToggle;
            string name = isHidden ? "Show side panel" : "Hide side panel";
            ToolTipService.SetToolTip(SidePanelToggleButton, name);
            AutomationProperties.SetName(SidePanelToggleButton, name);
        }

        private void OnSidePanelToggleClick(object sender, RoutedEventArgs e)
        {
            _chatView?.ToggleSidePanel();
        }
    }
}
```

- [ ] **Step 4: Call the initializer from the constructor**

In `gui-winui3/MainWindow.xaml.cs`, in the constructor after `InitializeSidebarHoverEffects();`, add:

```csharp
                InitializeSidePanelToggle();
```

- [ ] **Step 5: Drop a deleted session's surface state**

In `gui-winui3/MainWindow.SessionActions.cs`, inside `OnDeleteSessionClick`, immediately after the success check (`if (!outcome.Deleted && !outcome.Partial) { ... return; }`) and before the session-list refresh, add:

```csharp
                _chatView?.ForgetSidePanelState(sessionId);
```

- [ ] **Step 6: Build and run the tests**

Run: `dotnet build gui-winui3/RuApp.csproj -c Debug --nologo -v q`
Expected: Build succeeded.

Run: `dotnet test gui-winui3.Tests/gui-winui3.Tests.csproj -c Debug --nologo`
Expected: PASS — the whole GUI test project, including any MainWindow shell contracts that needed their expectations refreshed for the new named element (update only assertions whose decision intentionally changed).

- [ ] **Step 7: Commit**

```powershell
git add gui-winui3/MainWindow.xaml gui-winui3/MainWindow.SidePanel.cs gui-winui3/MainWindow.xaml.cs gui-winui3/MainWindow.SessionActions.cs gui-winui3.Tests/AgentSidePanelUiContractTests.cs
git commit -m "feat(gui): title bar toggle for the side surface"
```

---

### Task 8: Full verification, docs, and draft cleanup

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/operations/coding-agent-capabilities.md`
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/operations/verification.md` (only if the recorded total changes)
- Modify: `README.md` (only if the recorded total changes)
- Delete: `docs/plans/2026-10-08-right-side-panel-design.md`, `docs/plans/2026-10-08-right-side-panel.md`

- [ ] **Step 1: Run the full .NET matrix**

Run: `.\scripts\test_dotnet.ps1 -Configuration Debug`
Expected: all 12 projects green. Record the aggregate `N passed / 0 failed / 0 skipped`. (If exit code 75, another run holds the lock — wait and retry.)

- [ ] **Step 2: Run the Python guards**

Run: `.venv\Scripts\python.exe -m pytest tests -q`
Expected: all green. If `test_readme_counts.py` fails, that is Step 3's job.

- [ ] **Step 3: Record the run and sync the counts**

In `docs/operations/verification.md`, update the full-run record to the fresh aggregate in the file's established format (`**N passed / 0 failed / 0 skipped**` — match the existing line style around it). In `README.md`, update the badge (`tests-N%20passing`, with `%2C` for thousands separators) and the `12 projects, N tests` line so `tests/test_readme_counts.py` agrees. Re-run `.venv\Scripts\python.exe -m pytest tests -q` until green.

- [ ] **Step 4: Write the changelog entry**

Under `## [Unreleased]`, add as the first bullet:

```markdown
- **feat(gui)**: The right side surface becomes two tiers: the floating summary card stays (session title, +/− edit totals, compact background-task rows without Stop), and its chevron opens a DSH-style full-height details panel with closable Changes and Tasks tabs, a `+` reopen menu, a shared `+added −removed` header, expandable edit diffs, per-row task Stop through `/tasks kill`, drag resize (280–560 px, persisted to `side-panel.json`), and a fullscreen toggle; closing the last tab or the strip's collapse returns to the card. One title-bar toggle (the panel glyph) parked immediately left of the system caption buttons hides and restores the whole surface, with an `InputNonClientPointerSource` passthrough region and the caption `RightInset` respected. The Broker stays the only execution authority; the old corner show button is gone.
```

- [ ] **Step 5: Sync the operations docs**

In `docs/operations/coding-agent-capabilities.md`, replace the background-task row's `…; `/tasks` command, compact right-hand card and a bounded `background_tasks` prompt reference | … Live-provider long-task behavior and native card acceptance |` wording with:

`…; `/tasks` command, the compact right-hand card, a tabbed details panel (Changes, Tasks) with a title-bar visibility toggle, and a bounded `background_tasks` prompt reference | Live-provider long-task behavior and native card/panel acceptance |`

In `docs/operations/agent-tools.md`, replace the background-task paragraph's GUI sentence (`the GUI's compact right-hand card lists the same session tasks and stops one through the /tasks command.`) with:

`the GUI's compact right-hand card lists the same session tasks, and its details panel stops one through the /tasks command.`

(These two files already carry uncommitted wording from the previous GUI change; this edit lands on top and the commit in Step 7 includes them.)

- [ ] **Step 6: Delete the open drafts**

```powershell
Remove-Item docs/plans/2026-10-08-right-side-panel-design.md, docs/plans/2026-10-08-right-side-panel.md
```

- [ ] **Step 7: Commit**

```powershell
git add CHANGELOG.md README.md docs/operations/agent-tools.md docs/operations/coding-agent-capabilities.md docs/operations/verification.md
git add -u docs/plans
git commit -m "docs(gui): document the two-tier side surface and record the verification run"
```

---

## Self-review notes (resolved while writing)

- Spec coverage: states/transitions (Task 1), width persistence (Task 2), Changes tab (Task 3), resize math (Task 4), card + panel markup and rendering (Task 5), ChatView wiring incl. reserve and title-bar state mirror (Task 6), title-bar toggle incl. passthrough and caption inset (Task 4 + Task 7), session delete cleanup (Task 7), docs/changelog/verification (Task 8). Out of scope per spec is not built.
- The spec's "close (×) shown on the active chip and on hover" is implemented as an always-rendered close button (keyboard-reachable); update the spec text in Task 8 if the drafts were kept — they are deleted instead.
- Type names used across tasks match: `AgentSidePanelPolicy`/`AgentSidePanelMode`/`SidePanelTabKind`, `SidePanelPreferenceStore`, `ChangesTabRow`/`ChangesTabProjection`/`ChangesTabProjectionResult`, `TitleBarLayout`, `AgentSidePanelControl` (`SetSessionTitle`, `SetTasksStatus`, `RenderChanges`, `RenderTasks`, `ApplyState`, events), `ChatView.ToggleSidePanel`/`ForgetSidePanelState`, `MainWindow.UpdateSidePanelToggleState`.
