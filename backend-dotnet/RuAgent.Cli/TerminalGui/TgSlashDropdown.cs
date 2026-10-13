using RuAgent.Cli.Tui;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The slash-command suggestion list, anchored just above the composer. It draws through the
/// shared <see cref="TuiCard"/> row grammar at the full composer width, with command and
/// description columns aligned. This view drives the selection and its rendering.
/// The composer keeps focus the whole time the list is open, because typing must keep
/// filtering it. This view is therefore not focusable and takes no keys: the run loop routes
/// Up/Down, Enter/Tab and Escape here while every other character stays in the composer.
/// </summary>
public sealed class TgSlashDropdown : View
{
    /// <summary>Command rows shown at once; older rows scroll with the selection.</summary>
    public const int MaxVisible = 8;

    private IReadOnlyList<ChatCommandCatalog.SlashCommandInfo> _matches = [];
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

    /// <summary>Card height for the current match set, used by the owner to anchor the view.</summary>
    public int PanelHeight =>
        Math.Min(MaxVisible, Math.Max(1, _matches.Count)) + TuiCard.TuiCardFrameRows;

    public void SetMatches(IReadOnlyList<ChatCommandCatalog.SlashCommandInfo> matches)
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

        var width = Viewport.Width;
        var visible = Math.Max(1, Math.Min(MaxVisible, Viewport.Height - TuiCard.TuiCardFrameRows));
        var items = _matches
            .Select(match => new CardItem(TuiText.Pad(match.Command, 16) + match.Description))
            .ToList();
        var lines = TuiCard.ComposeList(
            width,
            "commands",
            "esc",
            items,
            _selected,
            visible,
            "↑↓ select · tab apply · esc close",
            fillWidth: true);
        var left = Math.Max(0, (Viewport.Width - width) / 2);
        for (var i = 0; i < lines.Count && i < Viewport.Height; i++)
        {
            SetAttribute(TgCard.Attribute(lines[i].Kind));
            Move(left, i);
            AddStr(lines[i].Text);
        }

        return true;
    }
}
