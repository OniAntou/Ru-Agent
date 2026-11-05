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
    public void Drag_revalidates_before_each_input_and_releases_after_target_change()
    {
        var events = new List<string>();
        var validations = new List<(int X, int Y)>();
        var deniedX = 2;

        var error = Assert.Throws<ComputerUseException>(() => PointerInputSequence.Drag(
            0, 0, 12, 0,
            (x, y) =>
            {
                validations.Add((x, y));
                if (x == deniedX)
                    throw new ComputerUseException("computer_app_not_granted", "overlay is not granted");
                return x == 0 ? (nint)1 : (nint)2;
            },
            (x, y, _) => events.Add($"move:{x},{y}"),
            (x, y, _) => events.Add($"down:{x},{y}"),
            (x, y, _) => events.Add($"up:{x},{y}"),
            () => { },
            CancellationToken.None));

        Assert.Equal("computer_app_not_granted", error.Code);
        Assert.Equal(new[] { "move:0,0", "down:0,0", "move:1,0", "up:1,0" }, events);
        Assert.Equal(new[] { (0, 0), (0, 0), (1, 0), (2, 0) }, validations);
    }

    [Fact]
    public void Drag_allows_separately_authorized_source_and_destination_windows()
    {
        var validatedRoots = new List<string>();
        var allowedRoots = new HashSet<string>(StringComparer.Ordinal) { "source", "destination" };

        PointerInputSequence.Drag(
            0, 0, 12, 0,
            (x, _) =>
            {
                var root = x < 12 ? "source" : "destination";
                Assert.Contains(root, allowedRoots);
                validatedRoots.Add(root);
                return root == "source" ? (nint)1 : (nint)2;
            },
            (_, _, _) => { }, (_, _, _) => { }, (_, _, _) => { }, () => { }, CancellationToken.None);

        Assert.Contains("source", validatedRoots);
        Assert.Contains("destination", validatedRoots);
    }

    [Fact]
    public void Drag_cancellation_releases_at_the_last_allowed_point_without_jumping_to_destination()
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<string>();

        Assert.Throws<OperationCanceledException>(() => PointerInputSequence.Drag(
            0, 0, 12, 0,
            (_, _) => (nint)7,
            (x, y, _) =>
            {
                events.Add($"move:{x},{y}");
                if (x == 1)
                    cancellation.Cancel();
            },
            (x, y, _) => events.Add($"down:{x},{y}"),
            (x, y, _) => events.Add($"up:{x},{y}"),
            () => { },
            cancellation.Token));

        Assert.Equal(new[] { "move:0,0", "down:0,0", "move:1,0", "up:1,0" }, events);
    }

    [Fact]
    public void Scroll_revalidates_before_each_scroll_input_and_stops_at_denied_target()
    {
        var events = new List<string>();
        var validations = 0;

        var error = Assert.Throws<ComputerUseException>(() => PointerInputSequence.Scroll(
            4, 5, 120, 240,
            (_, _) =>
            {
                if (++validations == 3)
                    throw new ComputerUseException("computer_app_not_granted", "overlay is not granted");
                return (nint)1;
            },
            (x, y, _) => events.Add($"move:{x},{y}"),
            (delta, _) => events.Add($"vertical:{delta}"),
            (delta, _) => events.Add($"horizontal:{delta}")));

        Assert.Equal("computer_app_not_granted", error.Code);
        Assert.Equal(new[] { "move:4,5", "vertical:240" }, events);
        Assert.Equal(3, validations);
    }

    [Fact]
    public async Task Drag_rechecks_broker_grants_during_the_path_and_releases_without_entering_denied_window()
    {
        var granted = Window("notepad.exe", Primary);
        var overlay = Window("mail.exe", Primary);
        var desktop = new FakeDesktop(granted) { WindowAtPoint = granted };
        var service = Service(desktop);
        await Grant(service, granted.AppName);
        await service.ExecuteAsync(Run, Session, true, true, Args("screenshot"), CancellationToken.None);
        var authorizations = 0;
        desktop.BeforePointerAuthorization = (kind, _, _) =>
        {
            if (kind == "drag" && ++authorizations == 3)
                desktop.WindowAtPoint = overlay;
        };

        var result = await service.ExecuteAsync(Run, Session, false, true,
            Args("drag", new JsonObject { ["x"] = 640, ["y"] = 360, ["to_x"] = 900, ["to_y"] = 360 }),
            CancellationToken.None);

        Assert.Equal("computer_app_not_granted", result.ErrorCode);
        Assert.Equal(new[] { "move", "down", "up" }, desktop.PointerEvents.Select(item => item.Kind));
        Assert.Equal(granted.Handle, desktop.PointerEvents[^1].Root);
    }

    [Fact]
    public async Task Drag_can_cross_between_two_separately_granted_windows()
    {
        var source = Window("notepad.exe", Primary);
        var destination = Window("paint.exe", Primary);
        var desktop = new FakeDesktop(source) { WindowAtPoint = source };
        var destinationX = ComputerGeometry.ToPhysical(Primary, 1280, 720, 900, 360).X;
        desktop.RootWindowLookup = (x, _) => x < destinationX ? source : destination;
        var service = Service(desktop);
        await Grant(service, source.AppName);
        await Grant(service, destination.AppName);
        await service.ExecuteAsync(Run, Session, true, true, Args("screenshot"), CancellationToken.None);

        var result = await service.ExecuteAsync(Run, Session, false, true,
            Args("drag", new JsonObject { ["x"] = 640, ["y"] = 360, ["to_x"] = 900, ["to_y"] = 360 }),
            CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Contains(desktop.PointerEvents, item => item.Root == source.Handle);
        Assert.Contains(desktop.PointerEvents, item => item.Root == destination.Handle);
    }

    [Fact]
    public async Task Scroll_rechecks_broker_grants_before_each_scroll_event()
    {
        var granted = Window("notepad.exe", Primary);
        var overlay = Window("mail.exe", Primary);
        var desktop = new FakeDesktop(granted) { WindowAtPoint = granted };
        var service = Service(desktop);
        await Grant(service, granted.AppName);
        await service.ExecuteAsync(Run, Session, true, true, Args("screenshot"), CancellationToken.None);
        var authorizations = 0;
        desktop.BeforePointerAuthorization = (kind, _, _) =>
        {
            if (kind == "scroll" && ++authorizations == 2)
                desktop.WindowAtPoint = overlay;
        };

        var result = await service.ExecuteAsync(Run, Session, false, true,
            Args("scroll", new JsonObject { ["x"] = 640, ["y"] = 360, ["direction"] = "down", ["amount"] = 2 }),
            CancellationToken.None);

        Assert.Equal("computer_app_not_granted", result.ErrorCode);
        Assert.Equal(new[] { "move" }, desktop.PointerEvents.Select(item => item.Kind));
    }

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
    public async Task Focus_brings_a_granted_app_forward_even_when_minimized()
    {
        var desktop = new FakeDesktop(Window("claude.exe", Primary)) { Foreground = Window("claude.exe", Primary) };
        desktop.Minimized.Add(Window("notepad.exe", new PixelRect(100, 100, 600, 500), "Notes"));
        var service = Service(desktop);
        var focus = Args("focus", new JsonObject { ["app"] = "Notepad" });

        Assert.Equal("computer_app_not_granted", (await service.ExecuteAsync(Run, Session, false, true, focus, CancellationToken.None)).ErrorCode);
        await Grant(service, "notepad.exe");
        var result = await service.ExecuteAsync(Run, Session, false, true, Args("focus", new JsonObject { ["app"] = "Notepad" }), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal("notepad.exe", desktop.Foreground!.AppName);
        Assert.Equal("Notes", result.Output["title"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("windowsterminal.exe", true, true, "computer_app_protected")]
    [InlineData("paint.exe", true, true, "computer_app_not_running")]
    [InlineData("notepad.exe", false, true, "computer_focus_refused")]
    [InlineData("notepad.exe", true, false, "computer_target_elevated")]
    public async Task Focus_fails_by_name(string app, bool focusSucceeds, bool notShielded, string code)
    {
        var desktop = new FakeDesktop(Window("notepad.exe", Primary), Window("windowsterminal.exe", Primary))
        {
            FocusSucceeds = focusSucceeds,
            Shielded = !notShielded,
        };
        var result = await Service(desktop).ExecuteAsync(Run, Session, true, true,
            Args("focus", new JsonObject { ["app"] = app }), CancellationToken.None);
        Assert.Equal(code, result.ErrorCode);
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

    [Fact]
    public async Task Without_image_input_the_screenshot_error_points_at_inspect()
    {
        var result = await Service(new FakeDesktop()).ExecuteAsync(Run, Session, false, false, Args("screenshot"), CancellationToken.None);
        Assert.Equal("image_unsupported_by_model", result.ErrorCode);
        Assert.Contains("action=inspect", result.SafeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Type_with_app_focuses_that_granted_app_before_typing()
    {
        var notepad = Window("notepad.exe", Primary);
        var desktop = new FakeDesktop(notepad) { Foreground = Window("ruapp.exe", Primary) };
        var service = Service(desktop);
        var typeIntoNotepad = Args("type", new JsonObject { ["text"] = "hi", ["app"] = "notepad.exe" });

        Assert.Equal("computer_app_not_granted", (await service.ExecuteAsync(Run, Session, false, true, typeIntoNotepad, CancellationToken.None)).ErrorCode);
        await Grant(service, "notepad.exe");
        var typed = await service.ExecuteAsync(Run, Session, false, true,
            Args("type", new JsonObject { ["text"] = "hi", ["app"] = "notepad.exe" }), CancellationToken.None);

        Assert.Null(typed.ErrorCode);
        Assert.Equal("hi", desktop.Typed);
        Assert.Equal("notepad.exe", desktop.Foreground!.AppName);
    }

    [Fact]
    public async Task Keyboard_without_app_names_the_protected_window_that_holds_focus()
    {
        var desktop = new FakeDesktop { Foreground = Window("ruapp.exe", Primary) };
        var service = Service(desktop);
        await Grant(service, "notepad.exe");

        var result = await service.ExecuteAsync(Run, Session, false, true, Args("key", new JsonObject { ["keys"] = "ctrl+l" }), CancellationToken.None);

        Assert.Equal("computer_app_protected", result.ErrorCode);
        Assert.Contains("pass app", result.SafeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Keyboard_with_an_app_that_loses_focus_fails_by_name()
    {
        var desktop = new FakeDesktop(Window("notepad.exe", Primary)) { FocusSucceeds = true };
        var service = Service(desktop);
        await Grant(service, "notepad.exe");
        desktop.AfterFocus = () => desktop.Foreground = Window("discord.exe", Primary);

        var result = await service.ExecuteAsync(Run, Session, false, true,
            Args("key", new JsonObject { ["keys"] = "ctrl+l", ["app"] = "notepad.exe" }), CancellationToken.None);

        Assert.Equal("computer_focus_refused", result.ErrorCode);
    }

    [Fact]
    public async Task Wait_reports_the_focused_app_and_shows_its_title_only_with_access()
    {
        var desktop = new FakeDesktop { Foreground = Window("firefox.exe", Primary, "Private page") };
        var service = Service(desktop);
        var wait = Args("wait", new JsonObject { ["ms"] = 100 });

        var hidden = await service.ExecuteAsync(Run, Session, false, true, wait, CancellationToken.None);
        Assert.Equal("firefox.exe", hidden.Output["focused_app"]!.GetValue<string>());
        Assert.Null(hidden.Output["focused_title"]);

        await Grant(service, "firefox.exe");
        var shown = await service.ExecuteAsync(Run, Session, false, true, Args("wait", new JsonObject { ["ms"] = 100 }), CancellationToken.None);
        Assert.Equal("Private page", shown.Output["focused_title"]!.GetValue<string>());
        Assert.Equal("computer_arguments_invalid",
            (await service.ExecuteAsync(Run, Session, false, true, Args("wait", new JsonObject { ["ms"] = 20_000 }), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task The_stop_hotkey_ends_a_wait()
    {
        var hotkey = new FakeHotkey();
        var service = Service(new FakeDesktop(), hotkey);
        var waiting = service.ExecuteAsync(Run, Session, true, true, Args("wait", new JsonObject { ["ms"] = 10_000 }), CancellationToken.None);
        await Task.Delay(100);
        hotkey.Press();

        var result = await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("computer_stopped_by_user", result.ErrorCode);
    }

    [Fact]
    public async Task Inspect_lists_only_visible_elements_of_the_granted_window_in_screenshot_coordinates()
    {
        var firefox = Window("firefox.exe", new PixelRect(0, 0, 1280, 1440), "Shop");
        var cover = Window("discord.exe", new PixelRect(1000, 0, 2560, 1440));
        var desktop = new FakeDesktop(cover, firefox);
        desktop.UiElements.AddRange(
        [
            new DesktopElement("Hyperlink", "Visible product", new PixelRect(100, 100, 300, 200), false, true),
            new DesktopElement("Hyperlink", "Visible product", new PixelRect(100, 100, 300, 200), false, true),
            new DesktopElement("Document", "Hidden tab", new PixelRect(0, 0, 1280, 1440), true, true),
            new DesktopElement("Button", "Under another window", new PixelRect(1100, 100, 1200, 200), false, true),
            new DesktopElement("Group", "", new PixelRect(10, 10, 20, 20), false, true),
            new DesktopElement("Button", "Disabled", new PixelRect(400, 400, 500, 440), false, false),
        ]);
        var service = Service(desktop);
        var inspect = Args("inspect", new JsonObject { ["app"] = "firefox.exe" });

        Assert.Equal("computer_app_not_granted", (await service.ExecuteAsync(Run, Session, false, true, inspect, CancellationToken.None)).ErrorCode);
        await Grant(service, "firefox.exe");
        var result = await service.ExecuteAsync(Run, Session, false, false, Args("inspect", new JsonObject { ["app"] = "firefox.exe" }), CancellationToken.None);

        Assert.Null(result.ErrorCode);
        Assert.Equal(1280, result.Output["width"]!.GetValue<int>());
        Assert.Equal(720, result.Output["height"]!.GetValue<int>());
        var elements = result.Output["elements"]!.AsArray();
        Assert.Equal(["Visible product", "Disabled"], elements.Select(element => element!["name"]!.GetValue<string>()));
        Assert.Equal(100, elements[0]!["x"]!.GetValue<int>());
        Assert.Equal(75, elements[0]!["y"]!.GetValue<int>());
        Assert.False(elements[1]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Inspect_sets_the_coordinate_frame_so_a_model_without_images_can_click()
    {
        var firefox = Window("firefox.exe", Primary, "Shop");
        var desktop = new FakeDesktop(firefox) { WindowAtPoint = firefox };
        desktop.UiElements.Add(new DesktopElement("Hyperlink", "Product", new PixelRect(200, 200, 400, 300), false, true));
        var service = Service(desktop);
        await Grant(service, "firefox.exe");

        var inspected = await service.ExecuteAsync(Run, Session, false, false, Args("inspect", new JsonObject { ["app"] = "firefox.exe" }), CancellationToken.None);
        var product = inspected.Output["elements"]!.AsArray()[0]!;
        var click = await service.ExecuteAsync(Run, Session, false, false,
            Args("click", new JsonObject { ["x"] = product["x"]!.GetValue<int>(), ["y"] = product["y"]!.GetValue<int>() }), CancellationToken.None);

        Assert.Null(click.ErrorCode);
        var (x, y, _, _) = desktop.LastClick!.Value;
        Assert.InRange(x, 200, 399);
        Assert.InRange(y, 200, 299);
    }

    [Theory]
    [InlineData("ruapp.exe", "computer_app_protected")]
    [InlineData("paint.exe", "computer_app_not_running")]
    public async Task Inspect_fails_by_name(string app, string code)
    {
        var result = await Service(new FakeDesktop(Window("notepad.exe", Primary))).ExecuteAsync(Run, Session, true, true,
            Args("inspect", new JsonObject { ["app"] = app }), CancellationToken.None);
        Assert.Equal(code, result.ErrorCode);
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
        public Func<int, int, DesktopWindow?>? RootWindowLookup { get; set; }
        public Action<string, int, int>? BeforePointerAuthorization { get; set; }
        public List<(string Kind, int X, int Y, nint Root)> PointerEvents { get; } = [];
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

        public DesktopWindow? RootWindowAt(int x, int y) => RootWindowLookup?.Invoke(x, y) ?? WindowAtPoint;
        public DesktopWindow? ForegroundWindow() => Foreground;

        public List<DesktopWindow> Minimized { get; } = [];
        public bool FocusSucceeds { get; set; } = true;
        public Action? AfterFocus { get; set; }

        public DesktopWindow? TopWindowOf(string appName) =>
            windows.Concat(Minimized).FirstOrDefault(window => window.AppName == appName);

        public bool BringToForeground(nint handle)
        {
            if (!FocusSucceeds) return false;
            Foreground = windows.Concat(Minimized).First(window => window.Handle == handle);
            AfterFocus?.Invoke();
            return true;
        }
        public bool IsShieldedFromInput(int processId) => Shielded;
        public void MovePointer(int x, int y) { }
        public void Click(int x, int y, PointerButton button, int count, nint expectedRoot) => LastClick = (x, y, button, count);
        public void Drag(int fromX, int fromY, int toX, int toY,
            Func<int, int, nint> authorizeTargetAt, CancellationToken cancellationToken) =>
            PointerInputSequence.Drag(
                fromX, fromY, toX, toY,
                (x, y) =>
                {
                    BeforePointerAuthorization?.Invoke("drag", x, y);
                    return authorizeTargetAt(x, y);
                },
                (x, y, root) => PointerEvents.Add(("move", x, y, root)),
                (x, y, root) => PointerEvents.Add(("down", x, y, root)),
                (x, y, root) => PointerEvents.Add(("up", x, y, root)),
                () => { },
                cancellationToken);

        public void Scroll(int x, int y, int deltaX, int deltaY, Func<int, int, nint> authorizeTargetAt) =>
            PointerInputSequence.Scroll(
                x, y, deltaX, deltaY,
                (pointX, pointY) =>
                {
                    BeforePointerAuthorization?.Invoke("scroll", pointX, pointY);
                    return authorizeTargetAt(pointX, pointY);
                },
                (pointX, pointY, root) => PointerEvents.Add(("move", pointX, pointY, root)),
                (delta, root) => PointerEvents.Add(("vertical", x, y, root)),
                (delta, root) => PointerEvents.Add(("horizontal", x, y, root)));

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

        public List<DesktopElement> UiElements { get; } = [];

        public IReadOnlyList<DesktopElement> Elements(nint window, CancellationToken cancellationToken) => UiElements;

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
