using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed class CommandPaletteViewModelTests
{
    private readonly List<string> _ran = new();
    private bool _runInHand;

    private IReadOnlyList<PaletteCommand> Commands() =>
    [
        new("new-chat", "New chat", "General", () => _ran.Add("new-chat"), shortcut: "Ctrl+N"),
        new("settings", "Open Settings", "General", () => _ran.Add("settings")),
        new("stop", "Stop the run", "Chat", () => _ran.Add("stop"), canRun: () => _runInHand),
        new("model", "Choose model", "Chat", () => _ran.Add("model")),
        .. Enumerable.Range(1, 10).Select(index => new PaletteCommand(
            $"session-{index}", $"Session {index}", CommandPaletteViewModel.SessionGroup, () => _ran.Add($"session-{index}"),
            hint: index == 10 ? @"D:\work\billing" : null)),
    ];

    private CommandPaletteViewModel NewPalette() => new(Commands);

    [Fact]
    public void Opening_lists_actions_then_the_most_recent_sessions_and_selects_the_first()
    {
        var palette = NewPalette();

        palette.Open();

        Assert.True(palette.IsOpen);
        Assert.Equal(["new-chat", "settings", "model"], palette.Results.Take(3).Select(command => command.Id));
        Assert.Equal(CommandPaletteViewModel.DefaultSessionCount, palette.Results.Count(command => command.Group == CommandPaletteViewModel.SessionGroup));
        Assert.Equal(0, palette.SelectedIndex);
        Assert.Equal("new-chat", palette.Selected?.Id);
    }

    [Fact]
    public void A_command_that_cannot_run_now_is_not_listed()
    {
        var palette = NewPalette();
        palette.Open();
        Assert.DoesNotContain(palette.Results, command => command.Id == "stop");

        _runInHand = true;
        palette.Open();
        Assert.Contains(palette.Results, command => command.Id == "stop");
    }

    [Fact]
    public void Typing_filters_in_order_and_ranks_word_starts_first()
    {
        var palette = NewPalette();
        palette.Open();

        palette.Query = "set";
        Assert.Equal("settings", palette.Results[0].Id);

        palette.Query = "nc";
        Assert.Equal("new-chat", palette.Results[0].Id);

        palette.Query = "zzz";
        Assert.Empty(palette.Results);
        Assert.True(palette.HasNoResults);
        Assert.Null(palette.Selected);
    }

    [Fact]
    public void Typing_searches_every_session_and_their_hints()
    {
        var palette = NewPalette();
        palette.Open();

        palette.Query = "billing";

        Assert.Equal("session-10", Assert.Single(palette.Results).Id);
    }

    [Fact]
    public void Arrow_keys_wrap_around_the_list()
    {
        var palette = NewPalette();
        palette.Open();
        int count = palette.Results.Count;

        palette.MoveSelection(-1);
        Assert.Equal(count - 1, palette.SelectedIndex);
        palette.MoveSelection(1);
        Assert.Equal(0, palette.SelectedIndex);
    }

    [Fact]
    public void Enter_closes_the_palette_and_then_runs_the_selected_command()
    {
        var palette = NewPalette();
        palette.Open();
        palette.Query = "model";
        bool wasOpenWhenRun = true;
        palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen)) wasOpenWhenRun = palette.IsOpen;
        };

        Assert.True(palette.ExecuteSelected());

        Assert.Equal(["model"], _ran);
        Assert.False(palette.IsOpen);
        Assert.False(wasOpenWhenRun);
    }

    [Fact]
    public void Enter_with_nothing_selected_does_nothing()
    {
        var palette = NewPalette();
        palette.Open();
        palette.Query = "zzz";

        Assert.False(palette.ExecuteSelected());
        Assert.True(palette.IsOpen);
        Assert.Empty(_ran);
    }

    [Fact]
    public void A_command_that_became_unrunnable_while_listed_is_not_run()
    {
        _runInHand = true;
        var palette = NewPalette();
        palette.Open();
        palette.Query = "stop";
        _runInHand = false;

        Assert.False(palette.ExecuteSelected());
        Assert.Empty(_ran);
    }

    [Theory]
    [InlineData("nc", "New chat", true)]
    [InlineData("chat", "New chat", true)]
    [InlineData("cn", "New chat", false)]
    [InlineData("", "", false)]
    public void Matching_needs_the_query_characters_in_order(string query, string text, bool matches)
    {
        Assert.Equal(matches, CommandPaletteViewModel.Score(query, text) is not null);
    }

    [Fact]
    public void A_word_start_match_outranks_a_match_inside_a_word()
    {
        Assert.True(CommandPaletteViewModel.Score("mod", "Choose model") > CommandPaletteViewModel.Score("mod", "Commodity"));
    }

    [Fact]
    public void Every_shortcut_is_listed_once()
    {
        Assert.Equal(
            KeyboardShortcutCatalog.All.Count,
            KeyboardShortcutCatalog.All.Select(shortcut => shortcut.Id).Distinct().Count());
        Assert.Equal(
            KeyboardShortcutCatalog.All.Count,
            KeyboardShortcutCatalog.All.Select(shortcut => shortcut.Keys).Distinct().Count());
        Assert.Equal("Ctrl+K", KeyboardShortcutCatalog.KeysFor(KeyboardShortcutCatalog.CommandPalette));
    }
}

public sealed class KeyboardShortcutWiringTests
{
    [Theory]
    [InlineData(KeyboardShortcutCatalog.CommandPalette, "VirtualKey.K")]
    [InlineData(KeyboardShortcutCatalog.FocusComposer, "VirtualKey.L")]
    [InlineData(KeyboardShortcutCatalog.ToggleSidePanel, "VirtualKey.J")]
    [InlineData(KeyboardShortcutCatalog.OpenSettings, "(VirtualKey)188")]
    [InlineData(KeyboardShortcutCatalog.ShowShortcuts, "(VirtualKey)191")]
    public void Every_listed_ctrl_shortcut_is_bound_by_the_shell(string id, string key)
    {
        // The sheet and the palette read the catalog; this pins that the shell binds the
        // same keys, so a shortcut cannot be documented and not work.
        Assert.StartsWith("Ctrl+", KeyboardShortcutCatalog.KeysFor(id));
        string handler = GuiSource.MethodBody(GuiSource.Text("MainWindow.CommandPalette.cs"), "TryHandleShellShortcut");
        Assert.Contains($"case {key}", handler, StringComparison.Ordinal);
        Assert.Contains("TryHandleShellShortcut(e.Key)", GuiSource.MethodBody(GuiSource.Text("MainWindow.Sessions.cs"), "OnShellPreviewKeyDown"), StringComparison.Ordinal);
    }
}

public sealed class TerminalKeyPassThroughTests
{
    [Fact]
    public void Shell_shortcuts_leave_terminal_editing_keys_to_a_focused_terminal()
    {
        // Ctrl+K, Ctrl+L and Ctrl+J are kill-line, clear and newline in a shell.
        string handler = GuiSource.MethodBody(GuiSource.Text("MainWindow.CommandPalette.cs"), "TryHandleShellShortcut");
        Assert.Contains("VirtualKey.K or VirtualKey.L or VirtualKey.J && IsFocusInsideTerminal()", handler, StringComparison.Ordinal);
        string probe = GuiSource.MethodBody(GuiSource.Text("MainWindow.CommandPalette.cs"), "IsFocusInsideTerminal");
        Assert.Contains("InteractiveTerminalControl", probe, StringComparison.Ordinal);
        Assert.Contains("TerminalPanelControl", probe, StringComparison.Ordinal);
    }
}
