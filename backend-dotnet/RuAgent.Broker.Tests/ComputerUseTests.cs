using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Computer;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ComputerUseTests
{
    private const string Run = "run_11111111111111111111111111111111";
    private const string OtherRun = "run_22222222222222222222222222222222";
    private const string Session = "session-computer";
    private static readonly PixelRect Primary = new(0, 0, 2560, 1440);

    [Fact]
    public void Visible_regions_keep_only_uncovered_parts_of_allowed_windows()
    {
        var top = Window("secret.exe", new PixelRect(100, 100, 300, 300));
        var granted = Window("notepad.exe", new PixelRect(0, 0, 400, 400));
        var regions = ComputerGeometry.VisibleAllowedRegions(
            [top, granted], window => window.AppName == "notepad.exe", new PixelRect(0, 0, 1000, 1000));

        Assert.Equal(400 * 400 - 200 * 200, regions.Sum(region => region.Width * region.Height));
        Assert.DoesNotContain(regions, region => !region.Intersect(top.Bounds).IsEmpty);
    }

    [Fact]
    public void Mask_blacks_out_everything_outside_the_kept_regions()
    {
        var monitor = new PixelRect(10, 10, 14, 12);
        var pixels = Enumerable.Repeat((byte)200, monitor.Width * monitor.Height * 4).ToArray();
        ComputerGeometry.MaskOutside(pixels, monitor, [new PixelRect(12, 10, 14, 11)]);

        // Row 0, columns 2 and 3 are kept; everything else is black.
        Assert.Equal(200, pixels[(0 * 4 + 2) * 4]);
        Assert.Equal(200, pixels[(0 * 4 + 3) * 4 + 2]);
        Assert.Equal(0, pixels[(0 * 4 + 1) * 4]);
        Assert.Equal(0, pixels[(1 * 4 + 3) * 4 + 1]);
    }

    [Fact]
    public void Screenshot_size_and_coordinates_map_back_to_physical_pixels()
    {
        var monitor = new PixelRect(-1920, 0, 0, 1080);
        var (width, height) = ComputerGeometry.ScreenshotSize(monitor, ComputerLimits.MaxScreenshotEdge);
        Assert.Equal((1280, 720), (width, height));
        Assert.Equal((-1920, 0), ComputerGeometry.ToPhysical(monitor, width, height, 0, 0));
        Assert.Equal((-1, 1079), ComputerGeometry.ToPhysical(monitor, width, height, 1279, 719));
        var error = Assert.Throws<ComputerUseException>(() => ComputerGeometry.ToPhysical(monitor, width, height, 1280, 0));
        Assert.Equal("computer_point_outside_screenshot", error.Code);
    }

    [Fact]
    public void Png_encoder_output_is_a_valid_image_of_the_requested_size()
    {
        var pixels = new byte[64 * 32 * 4];
        for (var index = 0; index < pixels.Length; index += 4) pixels[index + 2] = (byte)(index % 251);
        var png = ScreenshotEncoder.EncodePng(pixels, 64, 32, 16, 8);

        Assert.Equal("image/png", ToolOutputImage.SniffMediaType(png));
        Assert.Equal((16, 8), WorkspaceImageProbe.ReadDimensions("image/png", png));
    }

    [Theory]
    [InlineData("ctrl+s", 1, 0x53)]
    [InlineData("alt+tab", 1, 0x09)]
    [InlineData("shift+f10", 1, 0x79)]
    [InlineData("enter", 0, 0x0D)]
    [InlineData("ctrl+shift+Escape", 2, 0x1B)]
    public void Key_chords_parse_modifiers_and_one_key(string keys, int modifiers, int key)
    {
        var chord = KeyChord.Parse(keys);
        Assert.Equal(modifiers, chord.Modifiers.Count);
        Assert.Equal(key, chord.Key);
    }

    [Theory]
    [InlineData("ctrl+ctrl+s")]
    [InlineData("s+ctrl")]
    [InlineData("ctrl+")]
    [InlineData("hyper+s")]
    public void Invalid_key_chords_fail_by_name(string keys)
    {
        var error = Assert.Throws<ComputerUseException>(() => KeyChord.Parse(keys));
        Assert.Equal("computer_key_invalid", error.Code);
    }

    [Fact]
    public async Task List_shows_titles_only_for_granted_apps_and_marks_protected_ones()
    {
        var desktop = new FakeDesktop(
            Window("notepad.exe", new PixelRect(0, 0, 500, 500), "Notes - private"),
            Window("mail.exe", new PixelRect(500, 0, 900, 500), "Inbox - secret"),
            Window("ruapp.exe", new PixelRect(900, 0, 1200, 500), "Ru"));
        var service = Service(desktop);
        await Grant(service, "notepad.exe");

        var listed = await service.ExecuteAsync(Run, Session, false, true, Args("list"), CancellationToken.None);

        var apps = listed.Output["apps"]!.AsArray().Select(item => item!.AsObject()).ToDictionary(item => item["app"]!.GetValue<string>());
        Assert.Equal("granted", apps["notepad.exe"]["access"]!.GetValue<string>());
        Assert.Contains("Notes - private", apps["notepad.exe"]["titles"]!.ToJsonString());
        Assert.Null(apps["mail.exe"]["titles"]);
        Assert.Equal("protected", apps["ruapp.exe"]["access"]!.GetValue<string>());
    }

    [Fact]
    public async Task Request_access_refuses_protected_apps()
    {
        var service = Service(new FakeDesktop());
        var result = await service.ExecuteAsync(Run, Session, true, true,
            Args("request_access", new JsonObject { ["apps"] = new JsonArray("notepad.exe", "WindowsTerminal.exe") }), CancellationToken.None);

        Assert.Equal("computer_app_protected", result.ErrorCode);
        Assert.Empty(service.Grants(Session));
    }

    [Fact]
    public async Task Standard_screenshot_masks_apps_without_access_and_full_access_does_not()
    {
        var desktop = new FakeDesktop(
            Window("mail.exe", new PixelRect(0, 0, 100, 100)),
            Window("notepad.exe", new PixelRect(0, 0, 2560, 1440)));
        var service = Service(desktop);
        await Grant(service, "notepad.exe");

        var standard = await service.ExecuteAsync(Run, Session, false, true, Args("screenshot"), CancellationToken.None);
        Assert.True(standard.Output["masked"]!.GetValue<bool>());
        Assert.Equal(0, desktop.LastCapture![0]);
        Assert.Equal(255, desktop.LastCapture[(200 * 2560 + 200) * 4]);
        Assert.Equal("image/png", standard.Image!.MediaType);
        Assert.Equal(1280, standard.Output["width"]!.GetValue<int>());

        var full = await service.ExecuteAsync(Run, Session, true, true, Args("screenshot"), CancellationToken.None);
        Assert.False(full.Output["masked"]!.GetValue<bool>());
        Assert.Equal(255, desktop.LastCapture![0]);
    }

    [Fact]
    public async Task Screenshot_needs_a_model_that_accepts_images()
    {
        var result = await Service(new FakeDesktop()).ExecuteAsync(Run, Session, true, false, Args("screenshot"), CancellationToken.None);
        Assert.Equal("image_unsupported_by_model", result.ErrorCode);
    }

    [Fact]
    public async Task Pointer_actions_need_a_screenshot_and_reach_only_granted_unprotected_targets()
    {
        var desktop = new FakeDesktop(Window("notepad.exe", Primary)) { WindowAtPoint = Window("notepad.exe", Primary) };
        var service = Service(desktop);
        var click = Args("click", new JsonObject { ["x"] = 640, ["y"] = 360 });

        Assert.Equal("computer_screenshot_required", (await service.ExecuteAsync(Run, Session, false, true, click, CancellationToken.None)).ErrorCode);
        await service.ExecuteAsync(Run, Session, false, true, Args("screenshot"), CancellationToken.None);
        Assert.Equal("computer_app_not_granted", (await service.ExecuteAsync(Run, Session, false, true, click, CancellationToken.None)).ErrorCode);

        await Grant(service, "notepad.exe");
        var granted = await service.ExecuteAsync(Run, Session, false, true, click, CancellationToken.None);
        Assert.Null(granted.ErrorCode);
        Assert.Equal((1281, 721, PointerButton.Left, 1), desktop.LastClick);

        desktop.WindowAtPoint = Window("consent.exe", Primary);
        Assert.Equal("computer_app_protected", (await service.ExecuteAsync(Run, Session, true, true, click, CancellationToken.None)).ErrorCode);

        desktop.WindowAtPoint = Window("notepad.exe", Primary);
        desktop.Shielded = true;
        Assert.Equal("computer_target_elevated", (await service.ExecuteAsync(Run, Session, true, true, click, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Typing_stops_when_focus_moves_to_another_window()
    {
        var desktop = new FakeDesktop { Foreground = Window("notepad.exe", Primary) };
        var service = Service(desktop);
        await Grant(service, "notepad.exe");
        var typed = 0;
        desktop.OnType = () => { if (++typed == 3) desktop.Foreground = Window("ruapp.exe", Primary); };

        var result = await service.ExecuteAsync(Run, Session, false, true, Args("type", new JsonObject { ["text"] = "hello\n" }), CancellationToken.None);

        Assert.Equal("computer_target_changed", result.ErrorCode);
        Assert.Equal("he", desktop.Typed);
    }

    [Fact]
    public async Task A_window_hosting_the_ru_terminal_ui_is_never_a_target()
    {
        var host = Window("alacritty.exe", Primary);
        var desktop = new FakeDesktop(host) { Foreground = host };
        desktop.RuHosts.Add(host.ProcessId);
        var service = Service(desktop);

        var result = await service.ExecuteAsync(Run, Session, true, true, Args("key", new JsonObject { ["keys"] = "enter" }), CancellationToken.None);
        Assert.Equal("computer_app_protected", result.ErrorCode);
        var listed = await service.ExecuteAsync(Run, Session, true, true, Args("list"), CancellationToken.None);
        Assert.Contains("\"protected\"", listed.Output["apps"]!.ToJsonString());
    }

    [Fact]
    public async Task A_window_that_appears_during_capture_is_masked()
    {
        var granted = Window("notepad.exe", Primary);
        var desktop = new FakeDesktop(granted);
        var service = Service(desktop);
        await Grant(service, "notepad.exe");
        desktop.NextWindowLists.Enqueue([granted]);
        desktop.NextWindowLists.Enqueue([Window("toast.exe", new PixelRect(0, 0, 50, 50)), granted]);

        await service.ExecuteAsync(Run, Session, false, true, Args("screenshot"), CancellationToken.None);

        Assert.Equal(0, desktop.LastCapture![0]);
        Assert.Equal(255, desktop.LastCapture[(100 * 2560 + 100) * 4]);
    }

    [Fact]
    public async Task A_changed_monitor_layout_requires_a_new_screenshot()
    {
        var desktop = new FakeDesktop(Window("notepad.exe", Primary)) { WindowAtPoint = Window("notepad.exe", Primary) };
        var service = Service(desktop);
        await service.ExecuteAsync(Run, Session, true, true, Args("screenshot"), CancellationToken.None);
        desktop.MonitorBounds = new PixelRect(0, 0, 1920, 1080);

        var result = await service.ExecuteAsync(Run, Session, true, true, Args("move", new JsonObject { ["x"] = 1, ["y"] = 1 }), CancellationToken.None);
        Assert.Equal("computer_display_changed", result.ErrorCode);
    }

    [Fact]
    public async Task Keyboard_reaches_the_granted_foreground_window_and_the_windows_key_needs_explorer()
    {
        var desktop = new FakeDesktop { Foreground = Window("notepad.exe", Primary) };
        var service = Service(desktop);
        await Grant(service, "notepad.exe");

        Assert.Null((await service.ExecuteAsync(Run, Session, false, true, Args("type", new JsonObject { ["text"] = "hi" }), CancellationToken.None)).ErrorCode);
        Assert.Equal("hi", desktop.Typed);
        Assert.Equal("computer_key_not_allowed",
            (await service.ExecuteAsync(Run, Session, false, true, Args("key", new JsonObject { ["keys"] = "win+r" }), CancellationToken.None)).ErrorCode);
        Assert.Null((await service.ExecuteAsync(Run, Session, true, true, Args("key", new JsonObject { ["keys"] = "win+r" }), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task One_run_controls_the_desktop_until_it_is_released_or_idle()
    {
        var time = new ManualTime();
        var service = new ComputerUseService(new FakeDesktop(), new FakeHotkey(), time);
        await service.ExecuteAsync(Run, Session, true, true, Args("list"), CancellationToken.None);

        Assert.Equal("computer_busy", (await service.ExecuteAsync(OtherRun, Session, true, true, Args("list"), CancellationToken.None)).ErrorCode);
        time.Advance(ComputerUseService.OwnerIdleTimeout);
        Assert.Null((await service.ExecuteAsync(OtherRun, Session, true, true, Args("list"), CancellationToken.None)).ErrorCode);
        service.ReleaseRun(OtherRun);
        Assert.Null((await service.ExecuteAsync(Run, Session, true, true, Args("list"), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task The_stop_hotkey_cancels_the_action_and_stops_the_run()
    {
        var hotkey = new FakeHotkey();
        var desktop = new FakeDesktop { Foreground = Window("notepad.exe", Primary) };
        var service = Service(desktop, hotkey);
        string? stopped = null;
        service.StopRequested += run => stopped = run;
        desktop.OnType = () => hotkey.Press();

        var typed = await service.ExecuteAsync(Run, Session, true, true, Args("type", new JsonObject { ["text"] = "abc" }), CancellationToken.None);

        Assert.Equal("computer_stopped_by_user", typed.ErrorCode);
        Assert.Equal(Run, stopped);
        Assert.Equal("computer_stopped_by_user", (await service.ExecuteAsync(Run, Session, true, true, Args("list"), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Computer_use_stays_off_without_the_stop_hotkey_or_an_input_desktop()
    {
        var noHotkey = Service(new FakeDesktop(), new FakeHotkey { Registers = false });
        Assert.Equal("computer_stop_unavailable", (await noHotkey.ExecuteAsync(Run, Session, true, true, Args("list"), CancellationToken.None)).ErrorCode);

        var locked = Service(new FakeDesktop { DesktopAvailable = false });
        Assert.Equal("computer_desktop_unavailable", (await locked.ExecuteAsync(Run, Session, true, true, Args("list"), CancellationToken.None)).ErrorCode);
    }

    [Theory]
    [InlineData("notepad", "notepad.exe")]
    [InlineData(@"C:\Windows\System32\Notepad.EXE", "notepad.exe")]
    [InlineData("bad/..\\name?", null)]
    public void App_names_normalize_to_lowercase_executables(string input, string? expected) =>
        Assert.Equal(expected, ComputerProtectedApps.Normalize(input));

    private static async Task Grant(ComputerUseService service, string app) =>
        Assert.Null((await service.ExecuteAsync(Run, Session, false, true,
            Args("request_access", new JsonObject { ["apps"] = new JsonArray(app) }), CancellationToken.None)).ErrorCode);

    private static ComputerUseService Service(FakeDesktop desktop, FakeHotkey? hotkey = null) =>
        new(desktop, hotkey ?? new FakeHotkey());

    private static JsonObject Args(string action, JsonObject? extra = null)
    {
        var args = extra ?? new JsonObject();
        args["action"] = action;
        return args;
    }

    private static DesktopWindow Window(string app, PixelRect bounds, string title = "") =>
        new(app.GetHashCode(), bounds, Math.Abs(app.GetHashCode()) % 10_000 + 10, app, null, title);

    private sealed class FakeDesktop(params DesktopWindow[] windows) : IDesktopController
    {
        public PixelRect MonitorBounds { get; set; } = Primary;
        public DesktopWindow? WindowAtPoint { get; set; }
        public DesktopWindow? Foreground { get; set; }
        public bool Shielded { get; set; }
        public bool DesktopAvailable { get; set; } = true;
        public byte[]? LastCapture { get; private set; }
        public (int X, int Y, PointerButton Button, int Count)? LastClick { get; private set; }
        public string Typed { get; private set; } = "";
        public Action? OnType { get; set; }

        public bool IsInputDesktopAvailable() => DesktopAvailable;
        public IReadOnlyList<DesktopMonitor> Monitors() => [new DesktopMonitor(0, MonitorBounds, true)];
        public Queue<DesktopWindow[]> NextWindowLists { get; } = new();

        public IReadOnlyList<DesktopWindow> Windows() => NextWindowLists.Count > 0 ? NextWindowLists.Dequeue() : windows;

        public byte[] Capture(PixelRect bounds) =>
            LastCapture = Enumerable.Repeat((byte)255, bounds.Width * bounds.Height * 4).ToArray();

        public DesktopWindow? RootWindowAt(int x, int y) => WindowAtPoint;
        public DesktopWindow? ForegroundWindow() => Foreground;
        public bool IsShieldedFromInput(int processId) => Shielded;
        public void MovePointer(int x, int y) { }
        public void Click(int x, int y, PointerButton button, int count, nint expectedRoot) => LastClick = (x, y, button, count);
        public void Drag(int fromX, int fromY, int toX, int toY, CancellationToken cancellationToken) { }
        public void Scroll(int x, int y, int deltaX, int deltaY) { }

        public void TypeText(string text, nint expectedRoot, CancellationToken cancellationToken)
        {
            foreach (var character in text)
            {
                OnType?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (Foreground?.Handle != expectedRoot)
                    throw new ComputerUseException("computer_target_changed", "focus moved");
                Typed += character;
            }
        }

        public void PressChord(KeyChord chord, nint expectedRoot) { }

        public HashSet<int> RuHosts { get; } = [];

        public IReadOnlySet<int> RuHostProcessIds() => RuHosts;
    }

    private sealed class FakeHotkey : IComputerStopHotkey
    {
        public bool Registers { get; init; } = true;
        public event Action? Pressed;
        public bool EnsureRegistered() => Registers;
        public void Press() => Pressed?.Invoke();
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
