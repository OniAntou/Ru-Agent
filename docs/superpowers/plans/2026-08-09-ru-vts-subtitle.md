# Ru VTS Subtitle Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hiển thị karaoke subtitle (lời Ru đang đọc) ngay dưới model VTube Studio (VTS): overlay WinUI trong process RuApp, transparent/topmost/click-through, tự bám cửa sổ VTS, fade-out 2s khi hết.

**Architecture:** Tất cả là C# trong `gui-winui3/` (WinUI 3, .NET, Windows App SDK). Logic thuần (layout, timing, tracker, presenter) tách thành service riêng, unit-test bằng xUnit trong `gui-winui3.Tests/` (namespace `RuApp.Tests`, pattern đã có). Win32 qua `P/Invoke` để tìm/bám cửa sổ VTS (polling DispatcherTimer ~200ms). Subtitle overlay là `Window` thứ cấp (AppWindow, transparent, WS_EX_LAYERED|TRANSPARENT|NOACTIVATE). Nguồn text: `VoicePlaybackService` — mỗi sentence đã có sẵn text + các `VoiceAudioChunk` (có `SampleRate`) → duration = `Audio.Length / (SampleRate * 2)`.

## Global Constraints

- Không sửa backend Python; chỉ đọc dữ liệu có sẵn trong `VoicePlaybackService`.
- Không gửi transcript/nhắc; không thêm nuget package mới ngoài gói đang có (tránh suy xét SDK App).
- Tests: `dotnet test` trong `gui-winui3.Tests/` (xUnit); build: `& D:\LLM\build.bat`; chạy app: `start_dev.bat`.
- Naming: tên file/service theo pattern repo (`VoicePlaybackService`, `VtsWindowTracker`, v.v., PascalCase, namespace `RuApp.Services`).
- Không bỏ qua: mọi thay đổi VoicePlaybackService chỉ thêm 1 hook, không đổi hành vi play hiện tại.
- Toggle lưu persist cùng mechanism đã dùng cho "read replies aloud" trong VoiceView (kiểm ở Task 6).

---

### Task 1: `SubtitleLayout` — tính toán vị trí overlay

**Files:**
- Create: `gui-winui3/Services/SubtitleLayout.cs`
- Create: `gui-winui3.Tests/SubtitleLayoutTests.cs`

**Interfaces:**
- `public readonly record struct RectPx(int X, int Y, int Width, int Height)` (namespace `RuApp.Models`, cùng nơi `SettingsDto`; dùng chung cho cả app)
- `public static class SubtitleLayout`:
  - `public static (RectPx Rect, int FontSize, bool Overlap) ComputeSubtitleRect(RectPx vts, int screenWidth, int screenHeight)`
  - Font size: `clamp(14, (int)(vts.Height * 0.045), 34)`.
  - Rect: width = `Math.Clamp(vts.Width, 260, screenWidth * 0.32)`, x = `vts.X + (vts.Width - w)/2`; default y = `vts.Bottom + marginBottom`; nếu `y + overlayH > screenBottom - 4` → `y = vts.Bottom - overlayH - marginOverlap` (đè). Clamp x ≥ screenLeft + 4, ≤ screenRight - w - 4.

- [ ] **Step 1: Failing tests**

```csharp
// gui-winui3.Tests/SubtitleLayoutTests.cs
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SubtitleLayoutTests
{
    [Fact]
    public void FontSize_ScalesWithHeight_AndClamps()
    {
        Assert.Equal(14, SubtitleLayout.ComputeFontSize(300));
        Assert.Equal(34, SubtitleLayout.ComputeFontSize(1000));
        Assert.Equal(22, SubtitleLayout.ComputeFontSize(500));
    }

    [Fact]
    public void Rect_IsCenteredUnderWindow()
    {
        var r = SubtitleLayout.ComputeSubtitleRect(new RectPx(200, 100, 400, 500), 1920, 1080);
        Assert.Equal(400, r.Rect.Width);   // 400 (== vts width, không clamp: trong [260, 614])
        Assert.Equal(200, r.Rect.X);       // centered: 200 + (400 - 400) / 2
        Assert.Equal(100 + 500 + 12, r.Rect.Y);
    }

    [Fact]
    public void Rect_OverlapsWhenBottomExceedsScreen()
    {
        var r = SubtitleLayout.ComputeSubtitleRect(new RectPx(0, 600, 400, 400), 1920, 1000);
        Assert.True(r.Rect.Bottom <= 1000 - 4);
        Assert.True(r.Rect.Bottom < 600 + 400); // đè lên trên model (dưới vts.Bottom = 1000)
    }
}
```

(Trước khi hoàn thiện step: `RectPx` thêm auto-property `Bottom => Y + Height`.)

- [ ] **Step 2: Run to fail** — `dotnet test` trong `gui-winui3.Tests`; FAIL (missing types).
- [ ] **Step 3: Implement**

```csharp
// gui-winui3/Models/Models.cs (thêm record vào file có sẵn)
public readonly record struct RectPx(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

namespace RuApp.Services;

public static class SubtitleLayout
{
    private const int MarginBelow = 12;
    private const int MarginOverlap = 8;
    private const int ScreenEdge = 4;
    private const int MaxWidthRatioPercent = 32;

    public static int ComputeFontSize(int vtsHeight) =>
        Math.Clamp((int)Math.Round(vtsHeight * 0.045), 14, 34);

    public static (RectPx Rect, int FontSize, bool Overlap) ComputeSubtitleRect(
        RectPx vts, int screenWidth, int screenHeight)
    {
        int fontSize = ComputeFontSize(vts.Height);
        int overlayHeight = fontSize * 2 * 2;                  // ~2 dòng, dự phòng line spacing
        int overlayWidth = Math.Clamp(vts.Width, 260, screenWidth * MaxWidthPercent / 100);
        int x = vts.X + (vts.Width - overlayWidth) / 2;
        x = Math.Max(x, ScreenEdge);
        x = Math.Min(x, screenWidth - overlayWidth - ScreenEdge);

        int defaultY = vts.Bottom + MarginBelow;
        bool overlap = defaultY + overlayHeight > screenHeight - ScreenEdge;
        int y = overlap
            ? vts.Bottom - overlayHeight - MarginOverlap
            : defaultY;
        return (new RectPx(x, y, overlayWidth, overlayHeight), fontSize, overlap);
    }
}
```

- [ ] **Step 4: Run to pass** — tất cả tests xanh.
- [ ] **Step 5: Commit** (`feat(subtitle): overlay position layout`)

> **Fix note (controller, 2026-08-09):** test 3 ban đầu dùng `RectPx(0, 900, 400, 400)` — không thể pass với snippet được (model vượt khỏi màn hình 1000px: vts.Bottom = 1300 → overlap luôn cho Bottom = 1292 > 996). Đã sửa thành `RectPx(0, 600, 400, 400)` (vts.Bottom trùng screenHeight 1000) để giữ nguyên ý test overlap. Implementer thời điểm đó đã sửa test tương đương và ghi chú trong report — giữ nguyên kết quả đã chốt (implementation + test hiện tại pass).

---

### Task 2: `VtsWindowTracker` (Win32 P/Invoke + polling)

**Files:**
- Create: `gui-winui3/Services/VtsWindowTracker.cs`
- Create: `gui-winui3.Tests/VtsWindowTrackerTests.cs`

**Interfaces:**
- `public sealed class VtsWindowTracker : IDisposable` — ctor nhận `IVtsWindowLocator` (inject để test)
  - `VtsWindowSnapshot GetCurrent()` — trả `(bool Visible, RectPx Rect, int Dpi)` mỗi lần gọi (không tự poll; caller poll)
  - `public static class VtsWindowLocator` — implementation mặc định dùng P/Invoke:
    - `FindWindow(null, "VTube Studio")` (title cố định; fallback: `EnumWindows` lọc window có pid = process `VTubeStudio` name)
    - `GetWindowRect` (screen coords) + `DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT)` (chọn frame thật)
    - `GetMonitorInfo`/`MonitorFromRect` + `GetDpiForWindow(hWnd)` → dpi
  - `IsVisible => _visible`.

Tests inject fake locator (plain delegate / interface) — không đụng P/Invoke.

- [ ] **Step 1: Failing tests**

```csharp
// gui-winui3.Tests/VtsWindowTrackerTests.cs
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VtsWindowTrackerTests
{
    private sealed class FakeLocator : IVtsWindowLocator
    {
        public RectPx? Rect { get; set; }
        public VtsWindowInfo? GetCurrent() =>
            Rect is null ? null : new VtsWindowInfo(Rect.Value, 96);
    }

    [Fact]
    public void ReportsHidden_WhenNoWindow()
    {
        var locator = new FakeLocator { Rect = null };
        var tracker = new VtsWindowTracker(locator);
        Assert.False(tracker.GetCurrent().Visible);
    }

    [Fact]
    public void ReportsRect_WhenVisible()
    {
        var locator = new FakeLocator { Rect = new RectPx(10, 20, 300, 400) };
        var tracker = new VtsWindowTracker(locator);
        var current = tracker.GetCurrent();
        Assert.True(current.Visible);
        Assert.Equal(10, current.Rect.X);
    }
}
```

- [ ] **Step 2: Chạy fail** — `dotnet test`; FAIL (thiếu types).
- [ ] **Step 3: Implement** (P/Invoke trong `VtsWindowLocator`; `VtsWindowTracker` đơn giản wrapper). Lưu ý: `FindByProcessName` phải thực sự enum window và so pid với `Process.GetProcessesByName("VTubeStudio")`; nếu không có `GetMonitorInfo` (multi-monitor) thì dùng `Screen.PrimaryScreen` fallback:

```csharp
// gui-winui3/Services/VtsWindowTracker.cs
using System;
using System.Runtime.InteropServices;
using RuApp.Models;

namespace RuApp.Services;

public readonly record struct VtsWindowInfo(RectPx Rect, int Dpi);
public readonly record struct VtsWindowSnapshot(bool Visible, RectPx Rect, int Dpi);

public interface IVtsWindowLocator
{
    VtsWindowInfo? GetCurrent();
}

public sealed class VtsWindowLocator : IVtsWindowLocator
{
    private const string WindowTitle = "VTube Studio";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpProc, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public VtsWindowInfo? GetCurrent()
    {
        IntPtr hwnd = FindWindow(null, WindowTitle);
        if (hwnd == IntPtr.Zero)
        {
            hwnd = FindByProcessName("VTubeStudio");
        }
        if (hwnd == IntPtr.Zero) return null;

        GetWindowRect(hwnd, out RECT rect);
        int dpi = (int)GetDpiForWindow(hwnd);
        return new VtsWindowInfo(new RectPx(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top), dpi);
    }

    private static IntPtr FindByProcessName(string processName) { /* enum, lấy pid từ process; null nếu không */ return IntPtr.Zero; }
}

public sealed class VtsWindowTracker : IDisposable
{
    private readonly IVtsWindowLocator _locator;
    public VtsWindowTracker(IVtsWindowLocator locator) => _locator = locator;
    public VtsWindowSnapshot GetCurrent()
    {
        VtsWindowInfo? info = _locator.GetCurrent();
        return info is null
            ? new VtsWindowSnapshot(false, default, 96)
            : new VtsWindowSnapshot(true, info.Value.Rect, info.Value.Dpi);
    }
    public void Dispose() { }
}
```

- [ ] **Step 4: Run tests — pass**
- [ ] **Step 5: Commit** (`feat(subtitle): track VTube Studio window`)

---

### Task 3: `SubtitleTiming` (estimate theo chữ)

**Files:**
- Create: `gui-winui3/Services/SubtitleTiming.cs`
- Create: `gui-winui3.Tests/SubtitleTimingTests.cs`

**Interfaces:**
- `public static class SubtitleTiming`:
  - `public static IReadOnlyList<(string Token, int StartMs, int EndMs)> TokenizeWithTiming(string text, int durationMs)`
    - tách theo whitespace; bỏ token rỗng; `totalChars = sum(len(token))`
    - mỗi token: `tokenMs = durationMs * len(token) / totalChars` (int, làm tròn tích lũy để tổng = durationMs ± 1)
    - trả list theo thứ tự; punctuation giữ trong token (dấu phẩy tính tỷ lệ nhỏ tự nhiên)

- [ ] **Step 1: Failing test**

```csharp
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SubtitleTimingTests
{
    [Fact]
    public void Timing_SpreadsProportionalToLength()
    {
        var tokens = SubtitleTiming.TokenizeWithTiming("anh em khỏe", 900);
        Assert.Equal(3, tokens.Count);
        Assert.Equal(0, tokens[0].StartMs);
        Assert.Equal(900, tokens[^1].EndMs);
        Assert.True(tokens[0].EndMs <= tokens[1].StartMs);
    }

    [Fact]
    public void TotalDuration_IsPreserved()
    {
        var tokens = SubtitleTiming.TokenizeWithTiming("chào bạn xinh đẹp của em", 1000);
        Assert.Equal(1000, tokens[^1].EndMs);
        for (int i = 1; i < tokens.Count; i++)
            Assert.Equal(tokens[i - 1].EndMs, tokens[i].StartMs);
    }

    [Fact]
    public void EmptyText_ReturnsEmpty()
    {
        Assert.Empty(SubtitleTiming.TokenizeWithTiming("   ", 500));
    }
}
```

- [ ] **Step 2: Run — fail**
- [ ] **Step 3: Implement**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace RuApp.Services;

public sealed record TimedToken(string Token, int StartMs, int EndMs);

public static class SubtitleTiming
{
    public static IReadOnlyList<TimedToken> TokenizeWithTiming(string text, int durationMs)
    {
        var result = new List<TimedToken>();
        if (string.IsNullOrWhiteSpace(text) || durationMs <= 0) return result;

        string[] raw = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int totalChars = raw.Sum(t => t.Length);
        if (totalChars == 0) return result;

        int acc = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            int share = durationMs * raw[i].Length / totalChars;
            int start = acc; int end = acc + share;
            result.Add(new TimedToken(raw[i], start, end));
            acc = end;
        }
        // fix round-off: last token's EndMs = durationMs
        if (result.Count > 0)
        {
            var last = result[^1];
            result[^1] = last with { EndMs = durationMs };
        }
        return result;
    }
}
```

- [ ] **Step 4: Run — pass** (điều chỉnh round trong step nếu sum lệch >1ms)
- [ ] **Step 5: Commit** (`feat(subtitle): token timing estimator`)

---

### Task 4: `SubtitlePresenter` — state machine karaoke

**Files:**
- Create: `gui-winui3/Services/SubtitlePresenter.cs`
- Create: `gui-winui3.Tests/SubtitlePresenterTests.cs`

**Interfaces:**
- `public sealed class SubtitlePresenter : IDisposable`
  - `event Action<SubtitleSnapshot>? SnapshotChanged` — `SubtitleSnapshot(string VisibleText, string HighlightToken, int HighlightStartMs, int HighlightEndMs)`
  - `public void BeginSegment(string text, int durationMs)`
  - `public void CancelAll()`
  - `public void Tick(int elapsedMs)` (gọi bởi DispatcherTimer 50ms; test gọi trực tiếp) — tiến con trỏ theo elapsed
  - `public bool IsSpeaking`.
- Presenter phát `SnapshotChanged` mỗi token mới; `CancelAll()` → `SnapshotChanged(Empty)` (UI bắt đầu fade).

- [ ] **Step 1: Failing test**

```csharp
// gui-winui3.Tests/SubtitlePresenterTests.cs
using System;
using System.Collections.Generic;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SubtitlePresenterTests
{
    private sealed class Sink
    {
        public List<SubtitleSnapshot> Snapshots { get; } = new();
        public void OnSnapshot(SubtitleSnapshot s) => Snapshots.Add(s);
    }

    [Fact]
    public void SegmentsFlowFromFirstToLastToken()
    {
        var sink = new Sink();
        using var presenter = new SubtitlePresenter();
        presenter.SnapshotChanged += sink.OnSnapshot;
        presenter.BeginSegment("em là Ru", 900);

        presenter.Tick(0);
        presenter.Tick(300);
        presenter.Tick(700);
        presenter.Tick(900);

        Assert.True(sink.Snapshots[0].HighlightToken == "em");
        Assert.True(sink.Snapshots[1].HighlightToken == "là");
        Assert.True(sink.Snapshots[^1].HighlightToken == "Ru");
        Assert.True(sink.Snapshots[^1].IsSpeaking);
    }

    [Fact]
    public void CancelAll_StopsAndClears()
    {
        var sink = new Sink();
        using var presenter = new SubtitlePresenter();
        presenter.SnapshotChanged += sink.OnSnapshot;
        presenter.BeginSegment("a b c", 900);
        presenter.CancelAll();
        Assert.Equal(SubtitleSnapshot.Empty, presenter.Current);
    }
}
```

- [ ] **Step 2: Run — fail**
- [ ] **Step 3: Implement**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace RuApp.Services;

public readonly record struct SubtitleSnapshot(
    string VisibleText, string HighlightToken, int HighlightStartMs, int HighlightEndMs)
{
    public static readonly SubtitleSnapshot Empty = new("", "", 0, 0);
}

public sealed class SubtitlePresenter : IDisposable
{
    private IReadOnlyList<SubtitleToken> _tokens = Array.Empty<SubtitleToken>();
    private int _index;
    private int _elapsedMs;

    public event Action<SubtitleSnapshot>? SnapshotChanged;
    public SubtitleSnapshot Current { get; private set; } = SubtitleSnapshot.Empty;
    public bool IsSpeaking => _tokens.Count > 0;

    public void BeginSegment(string text, int durationMs)
    {
        _tokens = SubtitleTiming.TokenizeWithTiming(text, durationMs).Select(
            t => new SubtitleToken(t.Token, t.StartMs, t.EndMs)).ToArray();
        _index = 0;
        _elapsedMs = 0;
        Emit();
    }

    public void CancelAll()
    {
        _tokens = Array.Empty<SubtitleToken>();
        _index = 0;
        _elapsedMs = 0;
        Current = SubtitleSnapshot.Empty;
        SnapshotChanged?.Invoke(Current);
    }

    public void Tick(int elapsedMs)
    {
        if (_tokens.Count == 0 || elapsedMs < _elapsedMs) return;
        _elapsedMs = elapsedMs;
        if (_index < _tokens.Count - 1 && elapsedMs >= _tokens[_index + 1].EndMs)
        {
            _index++;
            Emit();
        }
    }

    private void Emit()
    {
        if (_tokens.Count == 0) return;
        var token = _tokens[_index];
        Current = new SubtitleSnapshot(
            string.Join(" ", _tokens.Select(t => t.Text)),
            token.Text, token.StartMs, token.EndMs);
        SnapshotChanged?.Invoke(Current);
    }

    public void Dispose() => SnapshotChanged = null;

    private readonly record struct SubtitleToken(string Text, int StartMs, int EndMs);
}
```

- [ ] **Step 4: Run — pass** (điều chỉnh test index nếu có)
- [ ] **Step 5: Commit** (`feat: subtitle presenter state machine`)

---

### Task 5: `SubtitleOverlayWindow` — WinUI overlay + `SubtitleOverlayService` giữa app

**Files:**
- Create: `gui-winui3/Views/SubtitleOverlayWindow.xaml` (khung: Grid trong suốt + `TextBlock` karaoke: Runs theo token, Brush đổi theo trạng thái)
- Create: `gui-winui3/Views/SubtitleOverlayWindow.xaml.cs` — `Show()`, `Hide()`, `MoveTo(RectPx)`, `ApplySnapshot(SubtitleSnapshot)` (highlight token), dùng `Window.AppWindow` + `SetWindowLongPtr` external flags.
- Create: `gui-winui3/Services/SubtitleOverlayService.cs` — quản lý vòng đời: DispatcherTimer 200ms polls `VtsWindowTracker` → di chuyển/resize/hide; DispatcherTimer 50ms ticks presenter → snapshot → update window.

**Behaviors:**
- Không tìm thấy VTS → `OverlayWindow.Hide()`, `_trackerHidden = true`.
- VTS maximize/minimize → `GetWindowRect` phản ánh; minimal → clamp trong layout (minimize: ShowWindow hidden check → hide).
- `Stop()` khi app tắt: dispose timers + close window.

- [ ] **Step 1:** Xây window XAML (body `Grid` transparent `Background="Transparent"`, `TextBlock x:Name="SubtitleText"` ở giữa, font từ layout, `VerticalAlignment=Center`, `TextWrapping=Wrap`, bóng: `TextBlock.DropShadow`/`OpacityMask` — dùng `Run` list + `TextBlock` để highlight bằng `Runs`).
- [ ] **Step 2:** Implement window code-behind: constructor tạo AppWindow (`AppWindow.Create(WindowId)`), set `IsShownInTaskbar=false`, `IsTopmost=true`, `Presenter` không owned Window; `SetWindowLongPtr(hwnd, GWL_EXSTYLE, WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_NOACTIVATE)`; `MoveAndResize`.
- [ ] **Step 3: Service** — `SubtitleOverlayService.Start(visualParent...)`, `SetText(SubtitleSnapshot s)` đổi Brush runs (đang đọc: `#FFD54F`, đã qua: `#66FFFFFF`, chưa: `#99FFFFFF`), `Stop()`.
- [ ] **Step 4:** Smoke test tay bằng app: tạo đối tượng service tạm, gọi `Show`, kéo VTS, thử text.
- [ ] **Step 5: Commit** (`feat: subtitle overlay window and service`)

---

### Task 6: Wire vào VoicePlaybackService + toggle tab Voice

**Files:**
- Modify: `gui-winui3/Services/VoicePlaybackService.cs`
- Modify: `gui-winui3/Views/VoiceView.xaml(.cs)` + (nếu cần) persistence settings
- Modify: `gui-winui3/MainWindow.xaml.cs` (khởi tạo overlay service khi app chạy)

**Hooks (không đổi hành vi):**
- `VoicePlaybackService` thêm optional `public SubtitleOverlayService? Subtitle { get; set; }`
- Trong `GenerateReplySentenceAsync`: sau khi có `sentence` sạch (chuẩn) + trước mỗi chunk: gom các chunk → `totalBytes`; durationMs = `totalBytes / (chunk.SampleRate * 2) * 1000` (mỗi chunk sampleRate biết; chunk.Format "wav"/"pcm").
- Gọi `Subtitle?.BeginSegment(sentence, durationMs)` trước khi play; `Subtitle?.CancelAll()` trong `StopVoice`/`FailReplyStream` (đã có hooks).
- Chưa đổi luồng play — đoạn này bọc ngoài.

**Toggle:** VoiceView thêm `ToggleSwitch SubtitleToggle` "Hiện subtitles trên model"; set từ/preferences như AutoRead (theo pattern hiện có — kiểm `VoiceView.xaml.cs` `AutoReadReplies` persist); bật → `SubtitleOverlayService.StartAsync`; tắt → `Stop`.

- [ ] **Step 1:** Đọc `VoiceView.xaml.cs` + `MainWindow.xaml.cs` (xác persistence toggle read-aloud hiện tại; làm theo).
- [ ] **Step 2:** Wire `VoicePlaybackService` — tìm điểm hook: trong `GenerateReplySentenceAsync` sau `TryQueueReplySentence` (sentence sạch + chunks) — chính xác: sau khi audio chunk đầu tiên của câu tới (có SampleRate) — set `Subtitle.BeginSegment(sentence, duration)`. `StopVoice`/`FailReplyStream` → `Subtitle?.CancelAll()`.
- [ ] **Step 3:** VoiceView toggle + persistence (pattern AutoReadReplies). `_overlayService = new SubtitleOverlayService(tracker, window)` trong MainWindow; `Start()` khi bật.
- [ ] **Step 4:** Build `build.bat`, smoke: khởi VTS + app, bật toggle, nói, quan sát overlay giữa màn hình VTS; kéo VTS; đóng VTS → ẩn; stop → dừng.
- [ ] **Step 5: Commit** (`feat: wire subtitles into read-aloud voice pipeline and settings`)

---

### Task 7: README + regression + final review

- [ ] **Step 1:** README: mục "Subtitle VTS": mô tả toggle, cơ chế bám VTS, limit (1-2 dòng, estimate theo duration, VTS phải mở, không ảnh hưởng Discord bridge).
- [ ] **Step 2:** `dotnet test` toàn bộ + `& D:\LLM\build.bat`.
- [ ] **Step 3:** Chay app, smoke: mở VTS, nói chuyện, kéo cửa sổ, check fade; tắt toggle.
- [ ] **Step 4: Commit** (`docs: subtitle VTS README`)

---