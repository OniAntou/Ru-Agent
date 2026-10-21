using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Windows.System;

namespace RuApp;

/// <summary>
/// The command palette and the keyboard shortcuts behind it. Every palette command
/// calls the handler the matching button or menu already uses.
/// </summary>
public sealed partial class MainWindow
{
    internal CommandPaletteViewModel Palette { get; private set; } = null!;

    private object? _paletteFocusReturn;

    private void InitializeCommandPalette()
    {
        Palette = new CommandPaletteViewModel(BuildPaletteCommands);
        Palette.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandPaletteViewModel.Selected)
                && Palette.Selected is { } selected
                && CommandPaletteList is not null)
            {
                CommandPaletteList.ScrollIntoView(selected);
            }
        };
    }

    private IReadOnlyList<PaletteCommand> BuildPaletteCommands()
    {
        var commands = new List<PaletteCommand>
        {
            new("new-chat", "New chat", "General",
                () => OnNewChatClick(NavNewChat, new RoutedEventArgs()),
                shortcut: KeyboardShortcutCatalog.KeysFor(KeyboardShortcutCatalog.NewChat), glyph: ""),
            new("focus-composer", "Focus the message box", "Chat",
                () => _chatView?.FocusComposer(),
                shortcut: KeyboardShortcutCatalog.KeysFor(KeyboardShortcutCatalog.FocusComposer),
                canRun: () => _chatView is not null, glyph: ""),
            new("model", "Choose model", "Chat",
                () => _chatView?.OpenModelPicker(),
                hint: "Model and reasoning for new turns",
                canRun: () => _chatView is not null, glyph: ""),
            new("stop", "Stop the run", "Chat",
                () => _ = StopRunFromPaletteAsync(),
                shortcut: "Esc", canRun: () => _chatView?.CanStopRun == true, glyph: ""),
            new("side-panel", Shell.IsSidePanelHidden ? "Show the side panel" : "Hide the side panel", "Chat",
                () => _chatView?.ToggleSidePanel(),
                shortcut: KeyboardShortcutCatalog.KeysFor(KeyboardShortcutCatalog.ToggleSidePanel),
                canRun: () => Shell.CanToggleSidePanel, glyph: ""),
            new("settings", "Open Settings", "General",
                OpenSettingsPopup,
                shortcut: KeyboardShortcutCatalog.KeysFor(KeyboardShortcutCatalog.OpenSettings), glyph: ""),
            new("settings-providers", "Models & Providers", "Settings",
                () => { OpenSettingsPopup(); _settingsView?.ShowProviders(); },
                hint: "Provider, API key and connection test",
                canRun: () => _settingsView is not null, glyph: ""),
            new("settings-updates", "Check for updates", "Settings",
                () => { OpenSettingsPopup(); _settingsView?.ShowUpdates(); },
                canRun: () => _settingsView is not null, glyph: ""),
            new("shortcuts", "Keyboard shortcuts", "General",
                () => _ = ShowShortcutsSheetAsync(),
                shortcut: KeyboardShortcutCatalog.KeysFor(KeyboardShortcutCatalog.ShowShortcuts), glyph: ""),
            new("theme", Shell.Theme == UiTheme.Dark ? "Switch to light theme" : "Switch to dark theme", "General",
                Shell.ToggleTheme, glyph: ""),
        };

        // Permission changes take the same path as the Settings dropdown. Full Access
        // still asks for confirmation before the next turn is sent.
        foreach (var (preset, title, hint) in new[]
                 {
                     (AgentPermissionPreset.Plan, "Permission: Plan", "Read only; no file changes or terminal"),
                     (AgentPermissionPreset.Standard, "Permission: Standard", "Workspace edits and terminal; approvals outside it"),
                     (AgentPermissionPreset.FullAccess, "Permission: Full Access", "Skips approvals; asks to confirm before the next turn"),
                 })
        {
            var target = preset;
            commands.Add(new PaletteCommand(
                $"preset-{target}", title, "Permission",
                () => Shell.ChoosePresetInSettings(target),
                hint: hint,
                canRun: () => _chatView is not null && _chatView.CurrentAgentPreset != target,
                glyph: target == AgentPermissionPreset.FullAccess ? "" : ""));
        }

        foreach (var session in Sidebar.Sessions
                     .Where(session => !string.IsNullOrWhiteSpace(session.Id))
                     .OrderByDescending(session => session.DisplayTime, StringComparer.Ordinal))
        {
            var target = session;
            commands.Add(new PaletteCommand(
                $"session-{target.Id}",
                string.IsNullOrWhiteSpace(target.Title) ? "Untitled chat" : target.Title,
                CommandPaletteViewModel.SessionGroup,
                () => _ = OpenSessionFromPaletteAsync(target),
                hint: target.WorkspacePath,
                canRun: () => !string.Equals(target.Id, Sidebar.ActiveSessionId, StringComparison.Ordinal),
                glyph: ""));
        }

        return commands;
    }

    private void OpenCommandPalette()
    {
        if (Palette.IsOpen || Shell.IsSettingsOpen || _isSessionUsageOpen) return;
        _paletteFocusReturn = FocusManager.GetFocusedElement(Content.XamlRoot);
        Palette.Open();
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        UiMotion.Enter(CommandPaletteOverlay);
        CommandPaletteQuery.Focus(FocusState.Keyboard);
    }

    private void CloseCommandPalette(bool restoreFocus = true)
    {
        if (Palette.IsOpen) Palette.Close();
        if (CommandPaletteOverlay.Visibility != Visibility.Visible) return;
        UiMotion.Exit(CommandPaletteOverlay, () =>
        {
            if (Palette.IsOpen) return;
            CommandPaletteOverlay.Visibility = Visibility.Collapsed;
            if (restoreFocus && _paletteFocusReturn is Control previous) previous.Focus(FocusState.Programmatic);
            _paletteFocusReturn = null;
        });
    }

    private void RunPaletteSelection()
    {
        // The command runs after the overlay starts closing, so a command that moves
        // focus (the composer, a flyout, Settings) keeps it.
        var selected = Palette.Selected;
        if (selected is null) return;
        CloseCommandPalette(restoreFocus: false);
        Palette.ExecuteSelected();
        if (!Palette.IsOpen && selected.Id.StartsWith("session-", StringComparison.Ordinal)) _chatView?.FocusComposer();
    }

    private void OnCommandPaletteQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                Palette.MoveSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                Palette.MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                RunPaletteSelection();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                CloseCommandPalette();
                e.Handled = true;
                break;
        }
    }

    private void OnCommandPaletteItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PaletteCommand command) return;
        Palette.Select(command);
        RunPaletteSelection();
    }

    private void OnCommandPaletteBackdropPointerPressed(object sender, PointerRoutedEventArgs e) => CloseCommandPalette();

    private async Task StopRunFromPaletteAsync()
    {
        try
        {
            if (_chatView is not null) await _chatView.StopRunAsync();
        }
        catch (Exception exception)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[CommandPalette] Stop failed: {exception.Message}");
        }
    }

    /// <summary>Opens a session the way selecting its sidebar row does.</summary>
    private async Task OpenSessionFromPaletteAsync(SessionInfo session)
    {
        if (_chatView is null) return;
        try
        {
            _chatView.SetRequestedWorkspace(Sidebar.Select(session));
            OnNavClick(NavChat, new RoutedEventArgs());
            await _chatView.OpenSessionAsync(session.Id, session.Title);
            SyncSidebarSelection(session.Id);
        }
        catch (Exception exception)
        {
            GuiDiagnosticWriter.WriteCrashEvent(exception);
            SetSessionActionError("Could not open the session");
        }
    }

    private async Task ShowShortcutsSheetAsync()
    {
        var grid = Views.KeyboardShortcutList.Build();
        var dialog = new ContentDialog
        {
            Title = "Keyboard shortcuts",
            Content = new ScrollViewer { Content = grid, MaxHeight = 460 },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        try
        {
            await ContentDialogGate.ShowAsync(dialog);
        }
        catch (Exception exception) when (ContentDialogGate.IsDialogConflict(exception))
        {
            // Another dialog is up; the shortcut can be pressed again once it closes.
        }
    }

    private bool IsFocusInsideTerminal()
    {
        var element = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
        while (element is not null)
        {
            if (element is Controls.InteractiveTerminalControl or Controls.TerminalPanelControl) return true;
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    /// <summary>
    /// Ctrl shortcuts from <see cref="KeyboardShortcutCatalog"/>. Returns whether the key
    /// was one of them. Ctrl+N, Ctrl+1/2 and Ctrl+arrows keep their own handling.
    /// </summary>
    private bool TryHandleShellShortcut(VirtualKey key)
    {
        // Ctrl+K, Ctrl+L and Ctrl+J mean kill-line, clear and newline in a shell. The
        // shell's handler sees keys first (preview events tunnel from the root), so it
        // leaves them to the integrated terminal while that has focus.
        if (key is VirtualKey.K or VirtualKey.L or VirtualKey.J && IsFocusInsideTerminal())
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.K:
                if (Palette.IsOpen) CloseCommandPalette(); else OpenCommandPalette();
                return true;
            case VirtualKey.L:
                OnNavClick(NavChat, new RoutedEventArgs());
                _chatView?.FocusComposer();
                return true;
            case VirtualKey.J:
                if (Shell.CanToggleSidePanel) _chatView?.ToggleSidePanel();
                return true;
            case (VirtualKey)188: // Ctrl+, (OEM comma)
                OpenSettingsPopup();
                return true;
            case (VirtualKey)191: // Ctrl+/ (OEM question)
                _ = ShowShortcutsSheetAsync();
                return true;
            default:
                return false;
        }
    }
}
