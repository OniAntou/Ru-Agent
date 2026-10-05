using RuAgent.Cli.Tui;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The slash-command suggestion list, anchored just above the composer. Rows, border and
/// footer are composed by the classic <see cref="SlashDropdown"/> so the copy cannot drift
/// from the classic engine; this view only positions, colours and drives the selection.
/// The composer keeps focus the whole time the list is open, because typing must keep
/// filtering it. This view is therefore not focusable and takes no keys: the run loop routes
/// Up/Down, Enter/Tab and Escape here while every other character stays in the composer.
/// </summary>
public sealed class TgSlashDropdown : View
{
    /// <summary>Command rows shown at once; older rows scroll with the selection.</summary>
    public const int MaxVisible = 8;

    private IReadOnlyList<InteractiveLineReader.SlashCommandInfo> _matches = [];
    private int _selected;

    public TgSlashDropdown()
    {
        CanFocus = false;
        SetScheme(TgPalette.Scheme(TuiToken.Ink));
    }

    /// <summary>Raised when the selected command is accepted (Enter/Tab).</summary>
    public event Action<string>? CommandAccepted;

    /// <summary>Raised when Escape dismisses the list.</summary>
    public event Action? Closed;

    public string? SelectedCommand => _matches.Count == 0 ? null : _matches[_selected].Command;

    /// <summary>Bordered height for the current match set, used by the owner to anchor the view.</summary>
    public int PanelHeight => Math.Min(MaxVisible, Math.Max(1, _matches.Count)) + 2;

    public void SetMatches(IReadOnlyList<InteractiveLineReader.SlashCommandInfo> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        _matches = matches;
        _selected = 0;
        SetNeedsDraw();
    }

    public void Move(int delta)
    {
        if (_matches.Count == 0 || delta == 0)
        {
            return;
        }

        var step = delta > 0 ? 1 : -1;
        for (var i = 0; i < Math.Abs(delta); i++)
        {
            _selected = ((_selected + step) % _matches.Count + _matches.Count) % _matches.Count;
        }

        SetNeedsDraw();
    }

    /// <summary>Accepts the highlighted command; returns it so the caller can run it.</summary>
    public string? Accept()
    {
        if (SelectedCommand is not { } command)
        {
            return null;
        }

        CommandAccepted?.Invoke(command);
        return command;
    }

    public void Close() => Closed?.Invoke();

    protected override bool OnDrawingContent(DrawContext? context)
    {
        if (Viewport.Width <= 0 || Viewport.Height <= 0)
        {
            return true;
        }

        var width = Math.Clamp(Viewport.Width, SlashDropdown.MinWidth, SlashDropdown.MaxWidth);
        var items = _matches
            .Select(match => new DropdownItem(match.Command, match.Description, match.Syntax))
            .ToList();
        var lines = SlashDropdown.Compose(
            width,
            "commands",
            items,
            _selected,
            MaxVisible,
            "↑↓ select · tab apply · esc close");
        for (var i = 0; i < lines.Count && i < Viewport.Height; i++)
        {
            var text = TuiText.Strip(lines[i]);
            var isSelectedRow = text.StartsWith("❯", StringComparison.Ordinal);
            var isFrame = i == 0 || i == lines.Count - 1;
            SetAttribute(TgPalette.Attr(isSelectedRow ? TuiToken.Brand : isFrame ? TuiToken.Brand : TuiToken.Meta));
            Move(0, i);
            AddStr(text);
        }

        return true;
    }
}
