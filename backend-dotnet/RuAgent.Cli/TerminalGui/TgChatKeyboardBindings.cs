using System.Text;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Routes composer, transcript and command-palette keys before TextField bindings.</summary>
internal sealed class TgChatKeyboardBindings
{
    private readonly TgChatWindow _window;
    private readonly Func<bool> _isBusy;
    private readonly Action _submit;
    private readonly Action _cancel;
    private readonly Action<int> _navigateHistory;
    private readonly Action _completeCommand;
    private readonly Action _paste;
    private IApplication? _keyboardApp;

    public TgChatKeyboardBindings(
        TgChatWindow window,
        Func<bool> isBusy,
        Action submit,
        Action cancel,
        Action<int> navigateHistory,
        Action completeCommand,
        Action paste)
    {
        _window = window;
        _isBusy = isBusy;
        _submit = submit;
        _cancel = cancel;
        _navigateHistory = navigateHistory;
        _completeCommand = completeCommand;
        _paste = paste;
        window.Composer.Input.KeyDown += OnComposerKey;
        window.IsRunningChanged += OnWindowIsRunningChanged;
    }

    private void OnComposerKey(object? sender, Key key)
    {
        if (key.Handled)
        {
            return;
        }

        if (TuiDebug.IsEnabled)
        {
            TuiDebug.Log(
                "keys",
                $"code={(int)key.KeyCode} rune={key.AsRune.Value} ctrl={key.IsCtrl} alt={key.IsAlt} shift={key.IsShift}");
        }

        if (TryHandleDropdownKey(key))
        {
            key.Handled = true;
            return;
        }

        if (_window.IsModalPanelOpen)
        {
            return;
        }

        if (TryHandleCommandPaletteKey(key))
        {
            key.Handled = true;
            return;
        }

        if (TryHandleTranscriptNavigationKey(key))
        {
            key.Handled = true;
            return;
        }

        if (IsSubmitKey(key))
        {
            _submit();
            key.Handled = true;
        }
        else if (key == Key.Esc && _isBusy())
        {
            _cancel();
            key.Handled = true;
        }
        else if (key == Key.CursorUp)
        {
            _navigateHistory(-1);
            key.Handled = true;
        }
        else if (key == Key.CursorDown)
        {
            _navigateHistory(1);
            key.Handled = true;
        }
        else if (key == Key.Tab)
        {
            _completeCommand();
            key.Handled = true;
        }
        else if (key == Key.V.WithCtrl)
        {
            _paste();
            key.Handled = true;
        }
        else if (key == Key.O.WithCtrl)
        {
            _window.Transcript.ToggleExpanded();
            key.Handled = true;
        }
    }

    private void OnWindowIsRunningChanged(object? sender, EventArgs<bool> args)
    {
        if (args.Value)
        {
            _keyboardApp = _window.App;
            if (_keyboardApp is not null)
            {
                _keyboardApp.Keyboard.KeyDown += OnApplicationKeyDown;
            }

            return;
        }

        if (_keyboardApp is not null)
        {
            _keyboardApp.Keyboard.KeyDown -= OnApplicationKeyDown;
            _keyboardApp = null;
        }
    }

    private void OnApplicationKeyDown(object? sender, Key key)
    {
        if (key.Handled || _window.IsModalPanelOpen)
        {
            return;
        }

        if (_window.IsCommandPaletteOpen)
        {
            if (TryHandleCommandPaletteKey(key))
            {
                key.Handled = true;
            }

            return;
        }

        if (IsTranscriptNavigationShortcut(key) && TryHandleTranscriptNavigationKey(key))
        {
            key.Handled = true;
        }
    }

    private bool TryHandleTranscriptNavigationKey(Key key)
    {
        if (key == Key.P.WithCtrl)
        {
            _window.OpenCommandPalette();
            return true;
        }

        if (key == Key.Home.WithCtrl)
        {
            _window.Transcript.ScrollToTop();
            return true;
        }

        if (key == Key.End.WithCtrl)
        {
            _window.Transcript.ScrollToTail();
            return true;
        }

        if (key.KeyCode == KeyCode.PageUp)
        {
            _window.Transcript.Page(-1);
            return true;
        }

        if (key.KeyCode == KeyCode.PageDown)
        {
            _window.Transcript.Page(1);
            return true;
        }

        return false;
    }

    private bool TryHandleCommandPaletteKey(Key key)
    {
        if (!_window.IsCommandPaletteOpen)
        {
            return false;
        }

        if (key == Key.Esc)
        {
            _window.DismissCommandPalette();
            return true;
        }

        if (key == Key.CursorUp || key == Key.CursorDown)
        {
            _window.MoveCommandPaletteSelection(key == Key.CursorUp ? -1 : 1);
            return true;
        }

        if (IsSubmitKey(key))
        {
            if (_isBusy())
            {
                _window.ShowCommandPaletteBusyHint();
            }
            else
            {
                _window.AcceptCommandPaletteSelection();
            }

            return true;
        }

        var query = _window.CommandPaletteQuery;
        if (key.KeyCode == KeyCode.Backspace)
        {
            _window.SetCommandPaletteQuery(RemoveLastRune(query));
            return true;
        }

        if (!key.IsCtrl && !key.IsAlt && Rune.TryCreate(key.AsRune.Value, out var rune) && !Rune.IsControl(rune))
        {
            _window.SetCommandPaletteQuery(query + rune);
        }

        return true;
    }

    private bool TryHandleDropdownKey(Key key)
    {
        if (!_window.IsSlashDropdownOpen)
        {
            return false;
        }

        if (key == Key.CursorUp)
        {
            _window.MoveSlashDropdown(-1);
            return true;
        }

        if (key == Key.CursorDown)
        {
            _window.MoveSlashDropdown(1);
            return true;
        }

        if (key == Key.Enter || key == Key.Tab)
        {
            _window.AcceptSlashDropdown();
            return true;
        }

        if (key == Key.Esc)
        {
            _window.HideSlashDropdown();
            return true;
        }

        return false;
    }

    private static bool IsTranscriptNavigationShortcut(Key key) =>
        key == Key.P.WithCtrl
        || key == Key.Home.WithCtrl
        || key == Key.End.WithCtrl
        || key.KeyCode is KeyCode.PageUp or KeyCode.PageDown;

    private static bool IsSubmitKey(Key key) => key.KeyCode == KeyCode.Enter || key.AsRune.Value is 13 or 10;

    private static string RemoveLastRune(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var last = value.EnumerateRunes().Last();
        return value[..^last.Utf16SequenceLength];
    }
}
