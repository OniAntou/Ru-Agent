# ASCII Meteor Empty Chat Background Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Add a sparse ASCII meteor background to Ru's empty Chat state and stop/clear it immediately when the first message exists.

**Architecture:** Create an isolated AsciiMeteorField WinUI Canvas control that owns a maximum of three ASCII TextBlock meteors, a low-frequency UI timer, and its lifecycle cleanup. Place it behind the existing welcome content and drive only its IsActive state from ChatView.UpdateViewMode(), which already derives state from Messages.Count.

**Tech Stack:** WinUI 3 XAML, C#, Canvas, TextBlock, DispatcherTimer, existing RuAccentBrush/RuMonoFontFamily resources, xUnit source/XAML contracts, sequential dotnet test and Release build.

---

## Scope and dirty-worktree boundary

The checkout currently contains unrelated user changes in:

- tests/test_cleanup_policy.py
- tools/maintenance/cleanup_policy.py

Do not stage, edit, reset, or delete those files. The already-local documentation commit e9ba207 is also outside this feature; preserve it and verify the feature files explicitly before committing.

## File map

- Create: gui-winui3/Controls/AsciiMeteorField.cs — isolated ASCII meteor control, timer, spawn/update loop, theme lookup, resize handling, and cleanup.
- Modify: gui-winui3/Views/ChatView.xaml — place the field behind the welcome panel and keep it non-interactive.
- Modify: gui-winui3/Views/ChatView.xaml.cs — bind field activation to the existing message-count view-mode path and dispose the field with the view.
- Create: gui-winui3.Tests/AsciiMeteorFieldContractTests.cs — source/XAML contracts for ASCII-only marks, bounded density, placement, state wiring, and lifecycle cleanup.
- Modify: docs/operations/runtime-behavior.md — document the presentation-only empty-state field and its stop-on-message boundary.
- Modify: docs/superpowers/specs/2026-08-21-ascii-meteor-empty-chat-design.md — mark the approved spec implemented after verification.

### Task 1: Add failing ASCII meteor contracts

**Files:**
- Create: gui-winui3.Tests/AsciiMeteorFieldContractTests.cs

- [ ] **Step 1: Write the failing control and view contracts**

Create the test file with these tests and source lookup helper:

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
        Assert.Contains("MeteorField.Dispose()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Meteor_control_is_sparse_ascii_only_and_has_explicit_cleanup()
    {
        var source = File.ReadAllText(FindSource("Controls", "AsciiMeteorField.cs"));

        Assert.Contains("class AsciiMeteorField : Canvas, IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxMeteorCount = 3", source, StringComparison.Ordinal);
        Assert.Contains("\"..-*\"", source, StringComparison.Ordinal);
        Assert.Contains("\"---*\"", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.Contains("private void StopAndClear()", source, StringComparison.Ordinal);
        Assert.Contains("_timer.Stop()", source, StringComparison.Ordinal);
        Assert.Contains("Children.Remove", source, StringComparison.Ordinal);
        Assert.DoesNotContain("emoji", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gradient", source, StringComparison.Ordinal);
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

- [ ] **Step 2: Run the focused contracts and verify red**

Run:

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AsciiMeteorFieldContractTests
~~~

Expected: the test assembly builds, then the two tests fail because AsciiMeteorField.cs and the ChatView markers do not exist yet. The failure must be a contract assertion or missing source path, not a project/build error.

- [ ] **Step 3: Commit the red contract slice**

Stage only the new test file and commit:

~~~powershell
git add gui-winui3.Tests\AsciiMeteorFieldContractTests.cs
git commit -m "test: define ASCII meteor empty-state contracts"
~~~

### Task 2: Implement the isolated AsciiMeteorField control

**Files:**
- Create: gui-winui3/Controls/AsciiMeteorField.cs

- [ ] **Step 1: Add the timer-backed Canvas control**

Create RuApp.Controls.AsciiMeteorField with this implementation shape. Keep the constants and marker strings exact so the contract describes the actual behavior rather than a visual intention:

~~~csharp
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RuApp.Controls;

public sealed class AsciiMeteorField : Canvas, IDisposable
{
    private const int MaxMeteorCount = 3;
    private const double TickSeconds = 0.06;
    private const double BaseOpacity = 0.22;

    private readonly DispatcherTimer _timer;
    private readonly List<Meteor> _meteors = new();
    private readonly Random _random = new();
    private bool _isLoaded;
    private bool _isActive;
    private bool _disposed;
    private double _spawnCountdown;

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
            _spawnCountdown = 1.0;
            _timer.Start();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isActive || !_isLoaded || _disposed) return;

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

        _spawnCountdown -= TickSeconds;
        if (_spawnCountdown <= 0 && _meteors.Count < MaxMeteorCount)
        {
            SpawnMeteor();
            _spawnCountdown = 3.5 + _random.NextDouble() * 2.5;
        }

        for (var index = _meteors.Count - 1; index >= 0; index--)
        {
            var meteor = _meteors[index];
            meteor.Age += TickSeconds;
            meteor.X += meteor.VelocityX * TickSeconds;
            meteor.Y += meteor.VelocityY * TickSeconds;
            Canvas.SetLeft(meteor.Element, meteor.X);
            Canvas.SetTop(meteor.Element, meteor.Y);
            meteor.Element.Opacity = OpacityFor(meteor.Age / meteor.Lifetime);

            if (meteor.Age >= meteor.Lifetime ||
                meteor.X > ActualWidth + 48 ||
                meteor.Y > ActualHeight + 48)
            {
                Children.Remove(meteor.Element);
                _meteors.RemoveAt(index);
            }
        }
    }

    private void SpawnMeteor()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var element = new TextBlock
        {
            Text = _random.Next(2) == 0 ? "..-*" : "---*",
            FontFamily = Application.Current.Resources["RuMonoFontFamily"] as FontFamily
                ?? new FontFamily("Consolas"),
            Foreground = Application.Current.Resources["RuAccentBrush"] as Brush
                ?? new SolidColorBrush(Color.FromArgb(255, 168, 75, 56)),
            FontSize = 12 + _random.Next(4),
            IsHitTestVisible = false,
            Opacity = BaseOpacity
        };

        var meteor = new Meteor
        {
            Element = element,
            X = _random.NextDouble() * Math.Max(1, ActualWidth * 0.42) - 24,
            Y = _random.NextDouble() * Math.Max(1, ActualHeight * 0.28),
            VelocityX = 80 + _random.NextDouble() * 50,
            VelocityY = 80 + _random.NextDouble() * 60,
            Lifetime = 2.2 + _random.NextDouble() * 1.6
        };

        Canvas.SetLeft(element, meteor.X);
        Canvas.SetTop(element, meteor.Y);
        Children.Add(element);
        _meteors.Add(meteor);
    }

    private static double OpacityFor(double progress)
    {
        if (progress < 0.15) return BaseOpacity * progress / 0.15;
        if (progress > 0.80) return BaseOpacity * (1 - progress) / 0.20;
        return BaseOpacity;
    }

    private void StopAndClear()
    {
        _timer.Stop();
        foreach (var meteor in _meteors)
        {
            Children.Remove(meteor.Element);
        }
        _meteors.Clear();
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

    private sealed class Meteor
    {
        public required TextBlock Element { get; init; }
        public double X { get; set; }
        public double Y { get; set; }
        public double VelocityX { get; init; }
        public double VelocityY { get; init; }
        public double Lifetime { get; init; }
        public double Age { get; set; }
    }
}
~~~

The control must not reference ChatView, Messages, ApiService, or any backend/service class. StopAndClear() must always stop the timer before removing children.

- [ ] **Step 2: Compile the isolated control**

Run:

~~~powershell
dotnet build gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore
~~~

Expected: the new control compiles with zero warnings and zero errors. The focused contract test remains red only because the view has not been wired yet.

- [ ] **Step 3: Commit the isolated control**

~~~powershell
git add gui-winui3\Controls\AsciiMeteorField.cs
git commit -m "feat: add sparse ASCII meteor field"
~~~

### Task 3: Wire the field to the empty Chat lifecycle

**Files:**
- Modify: gui-winui3/Views/ChatView.xaml
- Modify: gui-winui3/Views/ChatView.xaml.cs

- [ ] **Step 1: Place the control behind the welcome content**

Inside ReadingColumn, immediately before WelcomeHeroPanel, add:

~~~xml
<controls:AsciiMeteorField x:Name="MeteorField"
                           HorizontalAlignment="Stretch"
                           VerticalAlignment="Stretch"
                           IsHitTestVisible="False" />
~~~

Keep WelcomeHeroPanel after this element so normal XAML child ordering keeps the heading and suggestions above the meteor field. Do not add a background to the sidebar, InputContainer, or ChatScrollHost.

- [ ] **Step 2: Drive activation from UpdateViewMode()**

After the existing WelcomeHeroPanel and ChatScrollHost visibility updates, add this exact state update:

~~~csharp
if (MeteorField != null)
{
    MeteorField.IsActive = !hasMessages;
}
~~~

This keeps typing, sending, WebSocket deltas, STT, and backend events out of the animation decision. The existing Messages.CollectionChanged subscription continues to call UpdateViewMode() for both the first message and a cleared session.

- [ ] **Step 3: Dispose the field with ChatView**

At the end of ChatView.Dispose(), after stopping _typingDebounce and alongside
the active voice-conversation cleanup, add:

~~~csharp
MeteorField?.Dispose();
~~~

Do not replace the existing WebSocket unsubscription or voice cleanup.

- [ ] **Step 4: Run the focused contracts and verify green**

Run:

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AsciiMeteorFieldContractTests
~~~

Expected: 2/2 focused tests pass with zero failures. If XAML compilation fails, fix the namespace/ordering/reference without changing the contract boundaries.

- [ ] **Step 5: Commit the Chat wiring**

~~~powershell
git add gui-winui3\Views\ChatView.xaml gui-winui3\Views\ChatView.xaml.cs
git commit -m "feat: show ASCII meteors only in empty Chat"
~~~

### Task 4: Reconcile documentation and complete verification

**Files:**
- Modify: docs/operations/runtime-behavior.md
- Modify: docs/superpowers/specs/2026-08-21-ascii-meteor-empty-chat-design.md

- [ ] **Step 1: Update runtime behavior documentation**

In the presentation-only Chat signature note, add that the empty Chat state contains a sparse ASCII meteor field using the existing Ru accent token. State that it is active only while Messages.Count == 0, stops and clears on the first message, and does not call backend, WebSocket, STT, voice, subtitle, VTS, or memory paths.

- [ ] **Step 2: Mark the approved spec implemented**

Change the spec status line to:

~~~markdown
**Status:** Implemented 2026-08-21
~~~

Do not weaken its anti-particle, ASCII-only, sparse-density, or cleanup requirements.

- [ ] **Step 3: Run the complete WinUI test project**

Run sequentially:

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore
~~~

Expected: all tests pass with zero failures and zero errors.

- [ ] **Step 4: Build the self-contained Release app**

~~~powershell
dotnet build gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore
~~~

Expected: Build succeeded, 0 warnings, and 0 errors.

- [ ] **Step 5: Run UI and anti-slop checks**

Run:

~~~powershell
node C:\Users\USER\.agents\skills\impeccable\scripts\detect.mjs --json gui-winui3/Views/ChatView.xaml
rg -n -i "LinearGradientBrush|RadialGradientBrush|BlurEffect|emoji|starfield|particle" gui-winui3\Controls\AsciiMeteorField.cs gui-winui3\Views\ChatView.xaml
git diff --check
~~~

Expected: the UI detector returns []; the implementation scan returns no forbidden visual-effect/particle markers outside the deliberate ASCII strings; git diff --check is clean.

- [ ] **Step 6: Review the feature-only diff and commit docs**

Before staging, confirm the diff contains only the feature files listed in this plan plus the two documentation files. Do not stage the pre-existing cleanup files. Then commit:

~~~powershell
git add gui-winui3\Controls\AsciiMeteorField.cs gui-winui3\Views\ChatView.xaml gui-winui3\Views\ChatView.xaml.cs gui-winui3.Tests\AsciiMeteorFieldContractTests.cs docs\operations\runtime-behavior.md docs\superpowers\specs\2026-08-21-ascii-meteor-empty-chat-design.md
git commit -m "docs: record ASCII meteor empty-state behavior"
~~~

- [ ] **Step 7: Verify sync and leave manual visual acceptance to the user**

Run:

~~~powershell
git status --short --branch
git rev-list --left-right --count main...origin/main
~~~

If the feature commits are authorized for publication, push with:

~~~powershell
git push origin main
~~~

Do not launch or control RuApp automatically. The user must inspect the empty Chat field in both themes at 1440x900, 1024x768, and 800x600, then send a first message to confirm the field disappears immediately and does not restart during the conversation.
