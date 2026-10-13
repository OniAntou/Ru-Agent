using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Shell motion contract: the side surface and the two modals leave with a fade
/// before they collapse, screen switches dip-fade with the newest click winning,
/// and the composer's small panels enter once through the shared motion module —
/// every path degrading to the plain state change when animations are off.
/// </summary>
public sealed class ShellMotionContractTests
{
    [Fact]
    public void The_side_surface_fades_out_before_it_collapses()
    {
        var code = GuiSource.ViewText("ChatView");
        var layout = GuiSource.MethodBody(code, "UpdateSidePanelLayout");

        Assert.Contains("PlayVisibilityExit", layout, StringComparison.Ordinal);
        Assert.Contains("IsSidePanelDesiredVisible", layout, StringComparison.Ordinal);
        Assert.Contains("PlayVisibilityEntrance", layout, StringComparison.Ordinal);
        // The reserved space follows the last painted mode while the fade is in
        // flight, so the column cannot jump under a panel that is still visible.
        Assert.Contains("SidePanel.CurrentMode", layout, StringComparison.Ordinal);

        var control = GuiSource.Text("Controls", "AgentSidePanelControl.xaml.cs");
        var exit = GuiSource.MethodBody(control, "PlayVisibilityExit");
        Assert.Contains("UiTiming.AnimationsEnabled", exit, StringComparison.Ordinal);
        Assert.Contains("completed", exit, StringComparison.Ordinal);

        // A hide leaves the mode untouched, so a re-show must not rely on the
        // mode-change entrance.
        Assert.Contains("PlayVisibilityEntrance", control, StringComparison.Ordinal);
        var apply = GuiSource.MethodBody(control, "ApplyState");
        Assert.Contains("wasHidden", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void Modals_leave_with_a_fade_and_hand_focus_back_afterwards()
    {
        var code = GuiSource.ViewText("MainWindow");

        var settingsClose = GuiSource.MethodBody(code, "CloseSettingsPopup");
        Assert.Contains("UiMotion.Exit", settingsClose, StringComparison.Ordinal);
        Assert.Contains("SettingsModalOverlay.Visibility = Visibility.Collapsed", settingsClose, StringComparison.Ordinal);
        Assert.Contains("_isSettingsModalOpen", settingsClose, StringComparison.Ordinal);
        Assert.Contains("Focus", settingsClose, StringComparison.Ordinal);

        Assert.Contains("UiMotion.Enter", GuiSource.MethodBody(code, "OpenSettingsPopup"), StringComparison.Ordinal);

        var usageClose = GuiSource.MethodBody(code, "CloseSessionUsage");
        Assert.Contains("UiMotion.Exit", usageClose, StringComparison.Ordinal);
        Assert.Contains("SessionUsageOverlay.Visibility = Visibility.Collapsed", usageClose, StringComparison.Ordinal);
        Assert.Contains("_isSessionUsageOpen", usageClose, StringComparison.Ordinal);

        Assert.Contains("UiMotion.Enter", GuiSource.MethodBody(code, "OpenSessionUsageAsync"), StringComparison.Ordinal);
    }

    [Fact]
    public void Screen_switches_dip_fade_and_the_newest_click_wins()
    {
        var code = GuiSource.ViewText("MainWindow");

        var swap = GuiSource.MethodBody(code, "SwapNavContent");
        Assert.Contains("UiMotion.SwapContent", swap, StringComparison.Ordinal);
        Assert.Contains("_navSwapGeneration", swap, StringComparison.Ordinal);

        // The nav paths go through the swap: neither assigns the frame directly.
        foreach (var member in new[] { "OnNavClick", "OnToolsMenuItemClick", "NavigateToDiagnostics" })
        {
            var body = GuiSource.MethodBody(code, member);
            Assert.Contains("SwapNavContent", body, StringComparison.Ordinal);
            Assert.DoesNotContain("ContentFrame.Content =", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Small_composer_panels_enter_once_through_the_shared_motion()
    {
        var code = GuiSource.ViewText("ChatView");
        foreach (var member in new[]
                 {
                     "UpdateTodoPanel",
                     "ClearTodoPanel",
                     "UpdateAgentInlineState",
                     "ResetAgentState",
                     "UpdateQueuedTurnsPanel",
                 })
        {
            Assert.Contains("UiMotion.SetVisible", GuiSource.MethodBody(code, member), StringComparison.Ordinal);
        }

        var motion = GuiSource.Text("Services", "UiMotion.cs");
        foreach (var member in new[] { "Enter", "Exit", "SwapContent", "SetVisible" })
        {
            // Every entry point is one code path: the gate lives inside the module.
            var body = GuiSource.MethodBody(motion, member);
            Assert.True(
                body.Contains("AnimationsEnabled", StringComparison.Ordinal)
                || body.Contains("Enter(", StringComparison.Ordinal)
                || body.Contains("Visibility.Collapsed", StringComparison.Ordinal),
                $"{member} must gate or delegate");
        }

        Assert.Contains("UiTiming.AnimationsEnabled", GuiSource.MethodBody(motion, "Enter"), StringComparison.Ordinal);
        Assert.Contains("UiTiming.AnimationsEnabled", GuiSource.MethodBody(motion, "Exit"), StringComparison.Ordinal);
        Assert.Contains("UiTiming.AnimationsEnabled", GuiSource.MethodBody(motion, "SwapContent"), StringComparison.Ordinal);

        Assert.InRange(UiMotion.ExitMilliseconds, 60, 200);
        Assert.InRange(UiMotion.EntranceMilliseconds, 60, 240);
    }

    [Fact]
    public void Theme_flip_fades_through_instead_of_blinking()
    {
        var code = GuiSource.ViewText("MainWindow");

        var apply = GuiSource.MethodBody(code, "ApplyTheme");
        Assert.Contains("UiMotion.SwapContent", apply, StringComparison.Ordinal);
        Assert.Contains("ApplyThemeCore", apply, StringComparison.Ordinal);
        // The first apply is the startup theme and lands instantly, before the
        // window is up.
        Assert.Contains("_hasAppliedTheme", apply, StringComparison.Ordinal);

        var core = GuiSource.MethodBody(code, "ApplyThemeCore");
        Assert.Contains("ShellRoot.RequestedTheme", core, StringComparison.Ordinal);
        Assert.Contains("ApplyTitleBarTheme()", core, StringComparison.Ordinal);
    }

    [Fact]
    public void The_send_button_cross_fades_between_send_and_stop()
    {
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "UpdateSendButtonMode");

        Assert.Contains("UiMotion.SwapContent", body, StringComparison.Ordinal);
        // Only a real face change animates; repeat calls with the same face
        // re-apply instantly so a refresh storm cannot strobe the button.
        Assert.Contains("faceChanged", body, StringComparison.Ordinal);
        Assert.Contains("StopGlyph", body, StringComparison.Ordinal);
        Assert.Contains("SendGlyph", body, StringComparison.Ordinal);
    }
}
