using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
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

    /// <summary>Task 12's parked finding: the directory decisions must survive the TG path.</summary>
    [Fact]
    public void ApprovalPanel_ShowsFolderAndRememberChoices_WhenThePromptOffersTheDirectory()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.ShowApproval(ApprovalPrompt.For("external_directory_required", offerDirectory: true));
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("3 · folder", text);
            Assert.Contains("4 · always", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>
    /// I1: the picker wired for the TG slash commands. Arrow navigation and Enter must
    /// resolve the chosen item's value through <see cref="TgPanelRequests"/>.
    /// </summary>
    [Fact]
    public void Picker_ArrowAndEnter_ResolvesTheChosenValue()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var list = new ChoiceList("backend",
        [
            new ChoiceItem("OpenAI", string.Empty, "OpenAI"),
            new ChoiceItem("Anthropic", string.Empty, "Anthropic"),
        ]);
        Task<string?>? pending = null;

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            pending = TgPanelRequests.RequestChoiceAsync(window, list, CancellationToken.None);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            Assert.Contains("Anthropic", string.Join('\n', TgSnapshot.Capture(app)));
            app.InjectKey(Key.CursorDown);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            app.InjectKey(Key.Enter);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            // Fail fast rather than await a task a dropped key injection would never resolve.
            Assert.NotNull(pending);
            Assert.True(pending!.IsCompleted);
            Assert.Equal("Anthropic", pending.Result);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>
    /// I1: the masked secret panel accepts typed characters and resolves the exact value,
    /// while the screen only ever shows bullets.
    /// </summary>
    [Fact]
    public void Secret_TypedCharacters_ResolveTheMaskedValue()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        Task<string?>? pending = null;

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            pending = TgPanelRequests.RequestSecretAsync(window, "OpenAI", CancellationToken.None);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            app.InjectKey(new Key('s'));
            app.InjectKey(new Key('k'));
            app.InjectKey(new Key('-'));
            app.InjectKey(new Key('1'));
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.DoesNotContain("sk-1", text);
            Assert.Contains("•", text);
            app.InjectKey(Key.Enter);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(700), () =>
        {
            // Fail fast rather than await a task a dropped key injection would never resolve.
            Assert.NotNull(pending);
            Assert.True(pending!.IsCompleted);
            Assert.Equal("sk-1", pending.Result);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>I1: Escape on the picker resolves null so the command can fall back.</summary>
    [Fact]
    public void Picker_Escape_ResolvesNull()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var list = new ChoiceList("backend", [new ChoiceItem("OpenAI", string.Empty, "OpenAI")]);
        Task<string?>? pending = null;

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            pending = TgPanelRequests.RequestChoiceAsync(window, list, CancellationToken.None);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            app.InjectKey(Key.Esc);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(600), () =>
        {
            Assert.NotNull(pending);
            Assert.True(pending!.IsCompleted);
            Assert.Null(pending.Result);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
