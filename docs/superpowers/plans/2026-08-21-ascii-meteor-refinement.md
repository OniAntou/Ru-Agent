# ASCII Meteor Refinement Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax.

**Goal:** Replace the scattered random ASCII meteors with one coherent ..---* streak whose motion is clearly visible but sparse and restrained.

**Architecture:** Keep the existing AsciiMeteorField Canvas control and ChatView lifecycle wiring. Rewrite the control as a one-element state machine: an initial/rest phase, one fixed upper-left-to-lower-right travel lane, and explicit stop/clear cleanup. No backend, Chat message, or input path changes are needed.

**Tech Stack:** WinUI 3 XAML/C#, Canvas, one TextBlock, DispatcherTimer, existing RuAccentBrush and RuMonoFontFamily resources, xUnit source contracts, sequential Release test/build verification.

---

## Scope and dirty-worktree boundary

The current checkout has unrelated user edits in:

- tests/test_cleanup_policy.py
- tools/maintenance/cleanup_policy.py

Do not stage, edit, reset, delete, or include those files in any commit. The existing local design/implementation commits are preserved. This refinement will modify only the files listed below and will be pushed to main only after the final verification passes.

## File map

- Modify: gui-winui3.Tests/AsciiMeteorFieldContractTests.cs — replace the old three/random-meteor source contracts with one contiguous-streak contracts.
- Modify: gui-winui3/Controls/AsciiMeteorField.cs — replace random multi-element spawning with one fixed-lane streak state machine.
- Modify: docs/operations/runtime-behavior.md — describe the single streak and its presentation-only lifecycle.
- Modify: docs/superpowers/specs/2026-08-21-ascii-meteor-empty-chat-design.md — mark the refined design implemented after verification.
- Do not modify: gui-winui3/Views/ChatView.xaml or gui-winui3/Views/ChatView.xaml.cs; their existing placement, IsActive binding, and disposal already satisfy the refined design.

### Task 1: Replace the stale contracts with failing one-streak contracts

**Files:**
- Modify: gui-winui3.Tests/AsciiMeteorFieldContractTests.cs

- [ ] Step 1: Replace the test file with the refined contracts

Keep the existing source lookup helper and use this complete test content:

~~~csharp
using Xunit;

namespace RuApp.Tests;

public sealed class AsciiMeteorFieldContractTests
{
    [Fact]
    public void Chat_hosts_a_noninteractive_meteor_field_and_drives_it_from_messages()
    {
        var xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        var code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("controls:AsciiMeteorField", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MeteorField\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MeteorField.IsActive = !hasMessages", code, StringComparison.Ordinal);
        Assert.Contains("MeteorField?.Dispose()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Meteor_control_is_one_contiguous_ascii_streak_with_explicit_cleanup()
    {
        var source = File.ReadAllText(FindSource("Controls", "AsciiMeteorField.cs"));

        Assert.Contains("class AsciiMeteorField : Canvas, IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxMeteorCount = 1", source, StringComparison.Ordinal);
        Assert.Contains("private const string MeteorMark = \"..---*\"", source, StringComparison.Ordinal);
        Assert.Contains("private const double TravelSeconds = 1.8", source, StringComparison.Ordinal);
        Assert.Contains("private const double RestSeconds = 8.0", source, StringComparison.Ordinal);
        Assert.Contains("private TextBlock? _meteorElement", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.Contains("RectangleGeometry", source, StringComparison.Ordinal);
        Assert.Contains("private void BeginMeteor()", source, StringComparison.Ordinal);
        Assert.Contains("Canvas.SetLeft", source, StringComparison.Ordinal);
        Assert.Contains("Canvas.SetTop", source, StringComparison.Ordinal);
        Assert.Contains("private void StopAndClear()", source, StringComparison.Ordinal);
        Assert.Contains("_timer.Stop()", source, StringComparison.Ordinal);
        Assert.Contains("Children.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Random", source, StringComparison.Ordinal);
        Assert.DoesNotContain("List<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("emoji", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gradient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("particle", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(new[] { directory.FullName, "gui-winui3" }.Concat(parts).ToArray());
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
~~~

- [ ] Step 2: Run the focused contract test and verify it fails for the old design

Run:

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AsciiMeteorFieldContractTests
~~~

Expected: the test assembly builds, the Chat hosting test passes, and the control test fails because the current source still contains MaxMeteorCount = 3, the old marks, and Random/List<Meteor> implementation details. This must be a contract assertion failure, not a project or compiler failure.

- [ ] Step 3: Commit the failing refined contracts

Stage only the test file and commit:

~~~powershell
git add -- gui-winui3.Tests/AsciiMeteorFieldContractTests.cs
git commit -m "test: specify one-streak ASCII meteor behavior"
~~~

### Task 2: Rewrite the isolated control as one fixed-lane streak

**Files:**
- Modify: gui-winui3/Controls/AsciiMeteorField.cs

- [ ] Step 1: Replace the control with the one-element state machine

Replace the entire control with this implementation. It keeps the existing public IsActive/IDisposable surface used by ChatView, but removes random spawning and the collection of meteor elements:

~~~csharp
using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.Foundation;

namespace RuApp.Controls;

public sealed class AsciiMeteorField : Canvas, IDisposable
{
    private const int MaxMeteorCount = 1;
    private const double TickSeconds = 0.06;
    private const double InitialDelaySeconds = 1.6;
    private const double TravelSeconds = 1.8;
    private const double RestSeconds = 8.0;
    private const double BaseOpacity = 0.15;
    private const string MeteorMark = "..---*";

    private readonly DispatcherTimer _timer;
    private TextBlock? _meteorElement;
    private bool _isLoaded;
    private bool _isActive;
    private bool _isInFlight;
    private bool _disposed;
    private double _phaseRemaining;
    private double _progress;
    private double _startX;
    private double _startY;
    private double _endX;
    private double _endY;

    private int MeteorCount => _meteorElement is null ? 0 : 1;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_disposed || _isActive == value) return;
            _isActive = value;
            SyncActivity();
        }
    }

    public AsciiMeteorField()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        SyncActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        StopAndClear();
    }

    private void SyncActivity()
    {
        if (_disposed || !_isLoaded || !_isActive)
        {
            StopAndClear();
            return;
        }

        Visibility = Visibility.Visible;
        if (!_timer.IsEnabled)
        {
            _isInFlight = false;
            _progress = 0;
            _phaseRemaining = InitialDelaySeconds;
            _timer.Start();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isActive || !_isLoaded || _disposed) return;

        Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, ActualWidth, ActualHeight)
        };
        StopAndClear();
        SyncActivity();
    }

    private void OnTimerTick(object? sender, object e)
    {
        if (_disposed || !_isActive || !_isLoaded)
        {
            StopAndClear();
            return;
        }

        if (!_isInFlight)
        {
            _phaseRemaining -= TickSeconds;
            if (_phaseRemaining <= 0)
            {
                BeginMeteor();
            }

            return;
        }

        _progress += TickSeconds / TravelSeconds;
        if (_progress >= 1)
        {
            StopMeteor();
            _phaseRemaining = RestSeconds;
            return;
        }

        UpdateMeteorPosition();
    }

    private void BeginMeteor()
    {
        if (MeteorCount >= MaxMeteorCount || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _phaseRemaining = 1.0;
            return;
        }

        _startX = -56;
        _startY = Math.Clamp(ActualHeight * 0.12, 16, Math.Max(16, ActualHeight - 100));
        _endX = Math.Max(_startX + 140, ActualWidth * 0.82);
        _endY = Math.Min(ActualHeight - 36, _startY + Math.Max(64, ActualHeight * 0.18));
        if (_endY <= _startY)
        {
            _endY = _startY + 64;
        }

        _meteorElement = new TextBlock
        {
            Text = MeteorMark,
            FontFamily = Application.Current.Resources["RuMonoFontFamily"] as FontFamily
                ?? new FontFamily("Consolas"),
            Foreground = Application.Current.Resources["RuAccentBrush"] as Brush
                ?? new SolidColorBrush(Color.FromArgb(255, 168, 75, 56)),
            FontSize = 14,
            IsHitTestVisible = false,
            Opacity = 0
        };

        _progress = 0;
        _isInFlight = true;
        Canvas.SetLeft(_meteorElement, _startX);
        Canvas.SetTop(_meteorElement, _startY);
        Children.Add(_meteorElement);
        UpdateMeteorPosition();
    }

    private void UpdateMeteorPosition()
    {
        if (_meteorElement is null) return;

        double x = _startX + ((_endX - _startX) * _progress);
        double y = _startY + ((_endY - _startY) * _progress);
        Canvas.SetLeft(_meteorElement, x);
        Canvas.SetTop(_meteorElement, y);
        _meteorElement.Opacity = OpacityFor(_progress);
    }

    private void StopMeteor()
    {
        if (_meteorElement is not null)
        {
            Children.Remove(_meteorElement);
            _meteorElement = null;
        }

        _isInFlight = false;
        _progress = 0;
    }

    private static double OpacityFor(double progress)
    {
        if (progress < 0.12) return BaseOpacity * progress / 0.12;
        if (progress > 0.88) return BaseOpacity * (1 - progress) / 0.12;
        return BaseOpacity;
    }

    private void StopAndClear()
    {
        _timer.Stop();
        StopMeteor();
        _phaseRemaining = 0;
        Visibility = Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isActive = false;
        StopAndClear();
        _timer.Tick -= OnTimerTick;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        SizeChanged -= OnSizeChanged;
    }
}
~~~

The fixed lane starts just outside the left edge and ends in the upper reading area. The Math.Clamp/Math.Min bounds keep it usable on compact layouts while leaving the welcome heading and composer as the visual priority. StopAndClear stops the timer before removing the child and is used by inactive, unloaded, resized, and disposed paths.

- [ ] Step 2: Compile the isolated control

Run:

~~~powershell
dotnet build gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore
~~~

Expected: Build succeeded, 0 warnings, and 0 errors.

- [ ] Step 3: Run the focused contracts and verify green

Run:

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AsciiMeteorFieldContractTests
~~~

Expected: 2/2 tests pass.

- [ ] Step 4: Commit the rewritten control

Stage only the control and commit:

~~~powershell
git add -- gui-winui3/Controls/AsciiMeteorField.cs
git commit -m "refactor: make ASCII meteor a single coherent streak"
~~~

### Task 3: Reconcile runtime documentation and run the complete verification set

**Files:**
- Modify: docs/operations/runtime-behavior.md
- Modify: docs/superpowers/specs/2026-08-21-ascii-meteor-empty-chat-design.md

- [ ] Step 1: Update the runtime behavior signature note

Replace the current phrase a sparse ASCII meteor field in the empty Chat state with a single sparse ASCII meteor streak in the empty Chat state, and add that the streak is one contiguous ..---* mark that travels through a fixed lane, appears only while Messages.Count == 0, and stops/clears on the first message. Keep the existing presentation-only boundary that excludes backend, WebSocket, STT, voice, subtitle, VTS, and memory paths.

- [ ] Step 2: Mark the refined spec implemented

Change its status line from:

~~~markdown
**Status:** Draft refinement — awaiting user spec review
~~~

to:

~~~markdown
**Status:** Implemented 2026-08-21
~~~

Do not reintroduce the old three-meteor, random-cadence, or ..-*/---* requirements.

- [ ] Step 3: Run the complete WinUI test project

Run sequentially:

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore
~~~

Expected: 220+ tests pass, 0 failed, 0 errors. The exact total may increase if another independent test lands, but the run must report zero failures.

- [ ] Step 4: Build the self-contained Release app

~~~powershell
dotnet build gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore
~~~

Expected: Build succeeded, 0 warnings, and 0 errors.

- [ ] Step 5: Run UI and anti-slop checks

Run each command:

~~~powershell
node C:\Users\USER\.agents\skills\impeccable\scripts\detect.mjs --json gui-winui3/Views/ChatView.xaml
rg -n -i "LinearGradientBrush|RadialGradientBrush|BlurEffect|emoji|starfield|particle|new Random|List<" gui-winui3\Controls\AsciiMeteorField.cs gui-winui3\Views\ChatView.xaml
git diff --check
~~~

Expected: the UI detector returns []; the source scan returns no output; git diff --check exits successfully. Warnings about Git's LF/CRLF normalization are acceptable; whitespace errors are not.

- [ ] Step 6: Review the feature-only diff and commit documentation

Before staging, inspect:

~~~powershell
git diff -- gui-winui3.Tests/AsciiMeteorFieldContractTests.cs gui-winui3/Controls/AsciiMeteorField.cs docs/operations/runtime-behavior.md docs/superpowers/specs/2026-08-21-ascii-meteor-empty-chat-design.md
git status --short
~~~

Confirm the only dirty paths intended for this refinement are the four files listed in this task. Do not stage the two cleanup files. Then commit with this exact staging command:

~~~powershell
git add -- gui-winui3.Tests/AsciiMeteorFieldContractTests.cs gui-winui3/Controls/AsciiMeteorField.cs docs/operations/runtime-behavior.md docs/superpowers/specs/2026-08-21-ascii-meteor-empty-chat-design.md
git commit -m "docs: record refined ASCII meteor behavior"
~~~

- [ ] Step 7: Re-run the final test suite after the documentation commit

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore
~~~

Expected: zero failures and zero errors.

### Task 4: Verify integration state and leave visual acceptance to the user

- [ ] Step 1: Verify branch scope and synchronization

~~~powershell
git status --short --branch
git rev-list --left-right --count main...origin/main
~~~

Expected: the two pre-existing cleanup files remain visible as unstaged local edits, no feature file is unstaged, and the ahead/behind count is understood before pushing. Because the user authorized pushing directly to main, push only after the final green verification:

~~~powershell
git push origin main
~~~

- [ ] Step 2: Report manual acceptance boundaries

Do not launch or control RuApp automatically. Ask the user to inspect Light and Dark at 1440×900, 1024×768, and 800×600, confirm the ..---* motion is clearly visible but not distracting, confirm it stays out of the heading and composer, and send one message to confirm the streak stops and clears immediately.
