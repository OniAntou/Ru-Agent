using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgPanelsTests
{
    [Fact]
    public void SlashDropdown_ListsCommandsAboveTheComposer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.Input.Text = "/";
        window.ShowSlashDropdown();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("/model", string.Join('\n', rows));
            Assert.Contains("/attach", string.Join('\n', rows));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void ApprovalPanel_RendersChoicesAndEsc()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.ShowApproval("terminal_delete_requires_approval");
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("1 · once", text);
            Assert.Contains("2 · deny", text);
            Assert.Contains("3 · always", text);
            Assert.Contains("esc", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>
    /// Task 12's parked finding: the panel must render the caller's built prompt, never a
    /// rebuild from the 60-char truncated summary. A long summary that carries an always
    /// choice would otherwise lose it (the rebuild cannot match the full safe-code).
    /// </summary>
    [Fact]
    public void ApprovalPanel_KeepsCallerBuiltChoices_ForLongSummary()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var longSafeCode = new string('x', 80) + "_requires_approval";
        var prompt = new ApprovalPrompt(
            longSafeCode,
            [
                new ApprovalChoice("1 · once", "allow this call", ApprovalPrompt.AllowOnce),
                new ApprovalChoice("2 · deny", "abort this tool call", ApprovalPrompt.Deny),
                new ApprovalChoice("3 · always", "allow and remember for this project", ApprovalPrompt.AllowAlways),
            ]);
        window.ShowApproval(prompt);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("3 · always", text);
            Assert.DoesNotContain("3 · folder", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>Task 12's parked finding: the directory grant must survive the TG path.</summary>
    [Fact]
    public void ApprovalPanel_ShowsFolderChoice_WhenThePromptOffersTheDirectory()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.ShowApproval(ApprovalPrompt.For("external_directory_required", offerDirectory: true));
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("3 · folder", text);
            Assert.DoesNotContain("3 · always", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
