# Cinematic Meteor Shower Empty Chat Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax.

> **Status:** Historical implementation record. The vector shower shipped on
> 2026-08-21; the old ASCII proposal was retired. Current shell behavior lives
> in `docs/operations/runtime-behavior.md`.

**Goal:** Replace the rejected ASCII meteor control with a deterministic, vector-native five-meteor shower that feels cinematic in the empty Chat state and disappears immediately when a conversation starts.

**Status:** Implemented 2026-08-21

**Architecture:** Add a focused MeteorShowerField Canvas control with five reusable native-shape groups, a fixed MeteorSpec choreography, and an Idle/Burst/Rest state machine driven by one high-frequency DispatcherTimer. Rename the ChatView XAML/code lifecycle reference, delete the obsolete ASCII control and contracts, and keep the effect presentation-only.

**Tech Stack:** WinUI 3 XAML/C#, Canvas, Polygon, Line, CompositeTransform, DispatcherTimer, Stopwatch, existing RuAccentBrush/RuAccentSoftBrush/RuMutedBrush resources, xUnit source/XAML contracts, sequential Release test/build verification.

---

## Scope and dirty-worktree boundary

The checkout contains unrelated user edits in:

- tests/test_cleanup_policy.py
- tools/maintenance/cleanup_policy.py

Never stage, edit, reset, delete, or include those files. The superseded ASCII design/spec and implementation commits remain in history; the new work explicitly removes the obsolete ASCII runtime files and records the replacement design. Push only the feature commits after the complete verification set passes.

## File map

- Create: gui-winui3.Tests/MeteorShowerFieldContractTests.cs — source/XAML contracts for the new control and lifecycle.
- Delete: gui-winui3.Tests/AsciiMeteorFieldContractTests.cs — obsolete ASCII contracts.
- Create: gui-winui3/Controls/MeteorShowerField.cs — vector meteor groups, fixed sequence, state machine, clipping, and cleanup.
- Delete: gui-winui3/Controls/AsciiMeteorField.cs — obsolete TextBlock ASCII control.
- Modify: gui-winui3/Views/ChatView.xaml — host MeteorShowerField behind the welcome panel.
- Modify: gui-winui3/Views/ChatView.xaml.cs — drive/dispose the renamed control from the existing message-count path.
- Modify: docs/operations/runtime-behavior.md — describe the vector meteor shower instead of the ASCII streak.
- Modify: docs/superpowers/specs/2026-08-21-meteor-shower-empty-chat-design.md — mark the approved design implemented after verification.
- Do not modify: backend, WebSocket, STT, voice, subtitle, VTS, memory, composer, or message behavior.

### Task 1: Replace the obsolete ASCII contracts with failing shower contracts

**Files:**
- Delete: gui-winui3.Tests/AsciiMeteorFieldContractTests.cs
- Create: gui-winui3.Tests/MeteorShowerFieldContractTests.cs

- [ ] Step 1: Replace the test file with vector-shower contracts

Delete the old test file and create the new file with this complete content:

~~~csharp
using Xunit;

namespace RuApp.Tests;

public sealed class MeteorShowerFieldContractTests
{
    [Fact]
    public void Chat_hosts_a_noninteractive_meteor_shower_and_drives_it_from_messages()
    {
        var xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
        var code = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

        Assert.Contains("controls:MeteorShowerField", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MeteorShowerField\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MeteorShowerField.IsActive = !hasMessages", code, StringComparison.Ordinal);
        Assert.Contains("MeteorShowerField?.Dispose()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("AsciiMeteorField", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AsciiMeteorField", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Meteor_shower_is_vector_native_fixed_and_disposable()
    {
        var source = File.ReadAllText(FindSource("Controls", "MeteorShowerField.cs"));

        Assert.Contains("class MeteorShowerField : Canvas, IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxMeteorCount = 5", source, StringComparison.Ordinal);
        Assert.Contains("private static readonly MeteorSpec[] Burst", source, StringComparison.Ordinal);
        Assert.Contains("private sealed class MeteorVisual", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.Contains("Stopwatch", source, StringComparison.Ordinal);
        Assert.Contains("Polygon", source, StringComparison.Ordinal);
        Assert.Contains("Line", source, StringComparison.Ordinal);
        Assert.Contains("CompositeTransform", source, StringComparison.Ordinal);
        Assert.Contains("Idle", source, StringComparison.Ordinal);
        Assert.Contains("Burst", source, StringComparison.Ordinal);
        Assert.Contains("Rest", source, StringComparison.Ordinal);
        Assert.Contains("private void StopAndClear()", source, StringComparison.Ordinal);
        Assert.Contains("_timer.Stop()", source, StringComparison.Ordinal);
        Assert.Contains("Children.Remove", source, StringComparison.Ordinal);
        Assert.Contains("RectangleGeometry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TextBlock", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Ascii", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("new Random", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Gradient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("particle", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("emoji", source, StringComparison.OrdinalIgnoreCase);
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

- [ ] Step 2: Run the focused contracts and verify red

Run:

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~MeteorShowerFieldContractTests
~~~

Expected: the test assembly builds and the tests fail with source/XAML contract assertions because ChatView still references AsciiMeteorField and MeteorShowerField.cs does not exist. The failure must not be a project restore or compiler failure.

- [ ] Step 3: Commit only the failing contract slice

~~~powershell
git add -- gui-winui3.Tests/AsciiMeteorFieldContractTests.cs gui-winui3.Tests/MeteorShowerFieldContractTests.cs
git commit -m "test: specify cinematic meteor shower contracts"
~~~

### Task 2: Add the isolated vector meteor-shower control

**Files:**
- Create: gui-winui3/Controls/MeteorShowerField.cs

- [ ] Step 1: Add the fixed-sequence Canvas control

Create the new control with this complete implementation. Keep the old AsciiMeteorField.cs in place until the Chat wiring task so the project remains buildable between commits:

~~~csharp
using System;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace RuApp.Controls;

public sealed class MeteorShowerField : Canvas, IDisposable
{
    private const int MaxMeteorCount = 5;
    private const double LeadInSeconds = 1.0;
    private const double BurstDurationSeconds = 2.5;
    private const double RestSeconds = 10.0;
    private const double TickClampSeconds = 0.10;
    private const double TravelAngle = 18.0;

    private static readonly MeteorSpec[] Burst =
    {
        new(0.00, -0.16, 0.12, 0.42, 0.29, 1.10, 0.72, 0.14, 74),
        new(0.11, -0.12, 0.21, 0.88, 0.46, 0.86, 1.00, 0.28, 104),
        new(0.24, 0.04, 0.07, 0.94, 0.32, 0.98, 0.86, 0.22, 88),
        new(0.39, 0.22, 0.25, 1.06, 0.43, 1.18, 0.68, 0.12, 62),
        new(0.58, -0.04, 0.16, 0.98, 0.39, 0.82, 0.92, 0.26, 96)
    };

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly MeteorVisual[] _visuals;
    private bool _visualsAttached;
    private bool _isLoaded;
    private bool _isActive;
    private bool _disposed;
    private Phase _phase = Phase.Idle;
    private double _phaseElapsed;
    private double _lastClockSeconds;
    private int _nextMeteorIndex;

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

    public MeteorShowerField()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Clip = new RectangleGeometry();
        _visuals = CreateVisuals();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        RefreshClip();
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
            AttachVisuals();
            ResetSequence();
            _clock.Restart();
            _lastClockSeconds = 0;
            _timer.Start();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RefreshClip();
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

        double delta = ConsumeDelta();
        _phaseElapsed += delta;

        switch (_phase)
        {
            case Phase.Idle:
                if (_phaseElapsed >= LeadInSeconds)
                {
                    StartBurst();
                }
                break;
            case Phase.Burst:
                AdvanceBurst();
                if (_phaseElapsed >= BurstDurationSeconds)
                {
                    StartRest();
                }
                break;
            case Phase.Rest:
                if (_phaseElapsed >= RestSeconds)
                {
                    StartBurst();
                }
                break;
        }
    }

    private double ConsumeDelta()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double delta = Math.Clamp(now - _lastClockSeconds, 0, TickClampSeconds);
        _lastClockSeconds = now;
        return delta;
    }

    private void StartBurst()
    {
        _phase = Phase.Burst;
        _phaseElapsed = 0;
        _nextMeteorIndex = 0;
        foreach (var visual in _visuals)
        {
            visual.Hide();
        }
    }

    private void AdvanceBurst()
    {
        while (_nextMeteorIndex < Burst.Length &&
               _phaseElapsed >= Burst[_nextMeteorIndex].DelaySeconds)
        {
            _visuals[_nextMeteorIndex].Show();
            _nextMeteorIndex++;
        }

        for (var index = 0; index < _nextMeteorIndex; index++)
        {
            var spec = Burst[index];
            double localTime = _phaseElapsed - spec.DelaySeconds;
            if (localTime < 0 || localTime > spec.TravelSeconds)
            {
                if (localTime > spec.TravelSeconds)
                {
                    _visuals[index].Hide();
                }

                continue;
            }

            _visuals[index].Apply(spec, localTime / spec.TravelSeconds, ActualWidth, ActualHeight);
        }
    }

    private void StartRest()
    {
        _phase = Phase.Rest;
        _phaseElapsed = 0;
        _nextMeteorIndex = 0;
        foreach (var visual in _visuals)
        {
            visual.Hide();
        }
    }

    private void ResetSequence()
    {
        _phase = Phase.Idle;
        _phaseElapsed = 0;
        _nextMeteorIndex = 0;
        foreach (var visual in _visuals)
        {
            visual.Hide();
        }
    }

    private void RefreshClip()
    {
        Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, ActualWidth, ActualHeight)
        };
    }

    private MeteorVisual[] CreateVisuals()
    {
        Brush accent = Application.Current.Resources["RuAccentBrush"] as Brush
            ?? new SolidColorBrush(Color.FromArgb(255, 168, 75, 56));
        Brush soft = Application.Current.Resources["RuAccentSoftBrush"] as Brush
            ?? new SolidColorBrush(Color.FromArgb(150, 168, 75, 56));

        var visuals = new MeteorVisual[MaxMeteorCount];
        for (var index = 0; index < visuals.Length; index++)
        {
            visuals[index] = new MeteorVisual(accent, soft);
        }

        return visuals;
    }

    private void AttachVisuals()
    {
        if (_visualsAttached) return;

        foreach (var visual in _visuals)
        {
            Children.Add(visual.Layer);
        }

        _visualsAttached = true;
    }

    private void RemoveVisuals()
    {
        if (!_visualsAttached) return;

        foreach (var visual in _visuals)
        {
            Children.Remove(visual.Layer);
        }

        _visualsAttached = false;
    }

    private void StopAndClear()
    {
        _timer.Stop();
        _clock.Stop();
        _lastClockSeconds = 0;
        ResetSequence();
        RemoveVisuals();
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

    private enum Phase
    {
        Idle,
        Burst,
        Rest
    }

    private readonly record struct MeteorSpec(
        double DelaySeconds,
        double StartX,
        double StartY,
        double EndX,
        double EndY,
        double TravelSeconds,
        double Scale,
        double Opacity,
        double TailLength);

    private sealed class MeteorVisual
    {
        private const double HeadX = 116;
        private const double HeadY = 14;

        private readonly CompositeTransform _transform = new();
        private readonly Polygon _head;
        private readonly Line _mainTail;
        private readonly Line _middleTail;
        private readonly Line _farTail;

        public Canvas Layer { get; } = new()
        {
            Width = 132,
            Height = 28,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        public MeteorVisual(Brush accent, Brush soft)
        {
            _mainTail = new Line
            {
                Y1 = HeadY,
                Y2 = HeadY,
                Stroke = accent,
                StrokeThickness = 2.2
            };
            _middleTail = new Line
            {
                Y1 = HeadY,
                Y2 = HeadY,
                Stroke = soft,
                StrokeThickness = 1.6
            };
            _farTail = new Line
            {
                Y1 = HeadY,
                Y2 = HeadY,
                Stroke = soft,
                StrokeThickness = 1.0
            };
            _head = new Polygon
            {
                Fill = accent,
                Points = new PointCollection
                {
                    new Point(HeadX, 5),
                    new Point(HeadX + 9, HeadY),
                    new Point(HeadX, 23),
                    new Point(HeadX - 9, HeadY)
                }
            };

            Layer.RenderTransform = _transform;
            Layer.Children.Add(_farTail);
            Layer.Children.Add(_middleTail);
            Layer.Children.Add(_mainTail);
            Layer.Children.Add(_head);
        }

        public void Show()
        {
            Layer.Visibility = Visibility.Visible;
        }

        public void Hide()
        {
            Layer.Visibility = Visibility.Collapsed;
            Layer.Opacity = 0;
        }

        public void Apply(MeteorSpec spec, double progress, double width, double height)
        {
            double eased = EaseInOut(progress);
            double headX = Lerp(spec.StartX, spec.EndX, eased) * width;
            double headY = Lerp(spec.StartY, spec.EndY, eased) * height;
            double fade = progress < 0.10
                ? progress / 0.10
                : progress > 0.90
                    ? (1 - progress) / 0.10
                    : 1;

            double tailStart = HeadX - 4;
            double mainEnd = Math.Max(10, tailStart - spec.TailLength);
            double middleStart = tailStart - spec.TailLength * 0.48;
            double middleEnd = tailStart - spec.TailLength * 0.78;
            double farStart = tailStart - spec.TailLength * 0.74;
            double farEnd = Math.Max(4, tailStart - spec.TailLength);

            _mainTail.X1 = tailStart;
            _mainTail.X2 = mainEnd;
            _middleTail.X1 = middleStart;
            _middleTail.X2 = middleEnd;
            _farTail.X1 = farStart;
            _farTail.X2 = farEnd;

            _transform.CenterX = HeadX;
            _transform.CenterY = HeadY;
            _transform.Rotation = TravelAngle;
            _transform.ScaleX = spec.Scale;
            _transform.ScaleY = spec.Scale;
            Canvas.SetLeft(Layer, headX - HeadX);
            Canvas.SetTop(Layer, headY - HeadY);
            Layer.Opacity = spec.Opacity * fade;
        }

        private static double EaseInOut(double progress)
        {
            return progress < 0.5
                ? 4 * progress * progress * progress
                : 1 - Math.Pow(-2 * progress + 2, 3) / 2;
        }

        private static double Lerp(double start, double end, double progress)
        {
            return start + ((end - start) * progress);
        }
    }
}
~~~

The control uses fixed reusable groups, no random allocation during a burst, and
removes all groups from the Canvas when inactive. The sequence is normalized to
the control size and clipped to the reading area.

- [ ] Step 2: Compile the new control before Chat wiring

Run:

~~~powershell
dotnet build gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore
~~~

Expected: Build succeeded, 0 warnings, and 0 errors. The old Chat wiring remains in place for this checkpoint, so both controls compile.

- [ ] Step 3: Commit only the new control

~~~powershell
git add -- gui-winui3/Controls/MeteorShowerField.cs
git commit -m "feat: add deterministic vector meteor shower"
~~~

### Task 3: Wire the new control and remove the ASCII implementation

**Files:**
- Modify: gui-winui3/Views/ChatView.xaml
- Modify: gui-winui3/Views/ChatView.xaml.cs
- Delete: gui-winui3/Controls/AsciiMeteorField.cs

- [ ] Step 1: Replace the XAML host

Inside ReadingColumn, immediately before WelcomeHeroPanel, replace the old element with:

~~~xml
<controls:MeteorShowerField x:Name="MeteorShowerField"
                            HorizontalAlignment="Stretch"
                            VerticalAlignment="Stretch"
                            IsHitTestVisible="False" />
~~~

- [ ] Step 2: Replace the lifecycle references

In UpdateViewMode, replace the old activation block with:

~~~csharp
if (MeteorShowerField != null)
{
    MeteorShowerField.IsActive = !hasMessages;
}
~~~

In Dispose, replace the old cleanup call with:

~~~csharp
MeteorShowerField?.Dispose();
~~~

Keep the existing WebSocket unsubscriptions, typing debounce cleanup, and mic recorder disposal unchanged.

- [ ] Step 3: Delete the obsolete ASCII control

Delete gui-winui3/Controls/AsciiMeteorField.cs. Confirm no source/XAML reference to AsciiMeteorField remains:

~~~powershell
rg -n "AsciiMeteorField|..-\*|---\*" gui-winui3 gui-winui3.Tests
~~~

Expected: no output.

- [ ] Step 4: Run focused contracts and verify green

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~MeteorShowerFieldContractTests
~~~

Expected: 2/2 tests pass.

- [ ] Step 5: Commit the Chat wiring and obsolete-file removal

~~~powershell
git add -- gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3/Controls/AsciiMeteorField.cs
git commit -m "feat: wire cinematic meteor shower to empty Chat"
~~~

### Task 4: Reconcile runtime documentation and complete verification

**Files:**
- Modify: docs/operations/runtime-behavior.md
- Modify: docs/superpowers/specs/2026-08-21-meteor-shower-empty-chat-design.md

- [ ] Step 1: Update the runtime behavior signature note

Replace the current ASCII streak sentence with this behavior:

~~~markdown
The current Ru signature layer is also presentation-only: a quiet title-bar
wordmark, an editorial welcome eyebrow, message edge rules, and a choreographed
vector meteor shower in the empty Chat state. The shower uses five fixed
near/far lanes with the existing Ru accent tokens, runs only while
Messages.Count == 0, and stops and clears every visual group as soon as the
first message exists. The composer keeps the native TextBox caret/focus
behavior without a decorative accent line; there is no ambient welcome
rectangle or extra surface layer. These elements do not add Chat content,
VTS/voice metadata, fake status indicators, or calls to the backend, WebSocket,
STT, voice, subtitle, VTS, or memory paths.
~~~

- [ ] Step 2: Mark the new spec implemented (historical checklist item)

The implementation already updated the spec. This checklist text is retained
only as history; do not reopen the retired ASCII proposal.

Historical change:

~~~markdown
**Status:** Draft — awaiting user spec review
~~~

was changed to:

~~~markdown
**Status:** Implemented 2026-08-21
~~~

The old ASCII spec has since been removed from the working tree.

- [ ] Step 3: Run the full WinUI tests

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore
~~~

Expected: zero failures and zero errors.

- [ ] Step 4: Build the self-contained Release app

~~~powershell
dotnet build gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore
~~~

Expected: Build succeeded, 0 warnings, and 0 errors.

- [ ] Step 5: Run UI, anti-slop, and stale-reference checks

~~~powershell
node C:\Users\USER\.agents\skills\impeccable\scripts\detect.mjs --json gui-winui3/Views/ChatView.xaml
rg -n -i "AsciiMeteorField|LinearGradientBrush|RadialGradientBrush|BlurEffect|emoji|starfield|particle|new Random|TextBlock" gui-winui3\Controls\MeteorShowerField.cs gui-winui3\Views\ChatView.xaml gui-winui3\Views\ChatView.xaml.cs
git diff --check
~~~

Expected: UI detector returns []; the source/reference scan returns no
output; git diff --check exits successfully. Git LF/CRLF normalization
warnings are acceptable, whitespace errors are not.

- [ ] Step 6: Review feature-only diff and commit docs

Inspect:

~~~powershell
git diff -- gui-winui3.Tests/ gui-winui3/Controls/MeteorShowerField.cs gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs docs/operations/runtime-behavior.md docs/superpowers/specs/2026-08-21-meteor-shower-empty-chat-design.md
git status --short
~~~

Confirm only the intended feature/docs paths are staged; do not stage the two
cleanup files. Commit:

~~~powershell
git add -- gui-winui3/Controls/MeteorShowerField.cs gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/MeteorShowerFieldContractTests.cs docs/operations/runtime-behavior.md docs/superpowers/specs/2026-08-21-meteor-shower-empty-chat-design.md
git commit -m "docs: record cinematic meteor shower behavior"
~~~

- [ ] Step 7: Re-run full tests after the docs commit

~~~powershell
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj -c Release --no-restore
~~~

Expected: zero failures and zero errors.

### Task 5: Verify integration and leave visual acceptance to the user

- [ ] Step 1: Verify status and remote synchronization

~~~powershell
git status --short --branch
git rev-list --left-right --count main...origin/main
~~~

Expected: only the two pre-existing cleanup files remain unstaged, feature
files are clean, and the ahead/behind count is understood before pushing.

- [ ] Step 2: Push the authorized main branch

The user previously authorized direct main publication. After the final green
verification:

~~~powershell
git push origin main
~~~

- [ ] Step 3: Report manual acceptance boundaries

Do not launch or control RuApp automatically. Ask the user to inspect Light
and Dark at 1440×900, 1024×768, and 800×600; confirm the burst reads as a
cinematic five-streak shower, remains behind the welcome content, pauses long
enough between bursts, and clears immediately after the first message.
