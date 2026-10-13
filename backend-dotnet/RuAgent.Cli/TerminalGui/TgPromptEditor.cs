using System.Drawing;
using System.Globalization;
using System.Text;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>A multiline draft editor with a source-text cursor independent of wrapping.</summary>
#pragma warning disable CS0618 // Keep the shipped Terminal.Gui editor; a separate editor dependency is outside this repair.
public sealed class TgPromptEditor : TextView
{
    public const int MaxDraftChars = 16_000;
    private Point _sourceCursor;
    public event Action? DraftInsertionRejected;

    public TgPromptEditor()
    {
        Multiline = true;
        EnterKeyAddsLine = true;
        TabKeyAddsTab = false;
        WordWrap = true;
    }

    public new string Text
    {
        get => base.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        set => base.Text = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    // Keep callers working in source offsets; TextView's own cursor is in wrapped rune coordinates.
    public new int InsertionPoint
    {
        get
        {
            var lines = Text.Split('\n');
            var row = Math.Clamp(_sourceCursor.Y, 0, lines.Length - 1);
            var prefix = lines.Take(row).Sum(line => line.Length + 1);
            var elements = StringInfo.ParseCombiningCharacters(lines[row]);
            return prefix + (_sourceCursor.X < elements.Length ? elements[Math.Max(0, _sourceCursor.X)] : lines[row].Length);
        }
        set
        {
            var text = Text;
            var offset = Math.Clamp(value, 0, text.Length);
            if (offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset])) offset--;
            var before = text[..offset];
            var row = before.Count(character => character == '\n');
            var lastBreak = before.LastIndexOf('\n');
            var column = StringInfo.ParseCombiningCharacters(before[(lastBreak + 1)..]).Length;
            // Probe row starts through the public cursor API; toggling wrap would reset the cursor.
            var low = 0;
            var high = Math.Max(0, Lines - 1);
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                base.InsertionPoint = new Point(0, middle);
                if (_sourceCursor.Y < row || (_sourceCursor.Y == row && _sourceCursor.X <= column)) low = middle;
                else high = middle - 1;
            }
            base.InsertionPoint = new Point(0, low);
            var startColumn = _sourceCursor.X;
            base.InsertionPoint = new Point(Math.Clamp(column - startColumn, 0, GetLine(low).Count), low);
        }
    }

    public bool IsAtFirstVisualRow => CurrentRow == 0;
    public bool IsAtLastVisualRow => CurrentRow >= Lines - 1;

    public bool TryInsertDraft(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var cleaned = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
            if (!Rune.IsControl(rune) || rune.Value is '\n' or '\t') cleaned.Append(rune);
        var selectedLength = SelectedText.Replace("\r\n", "\n", StringComparison.Ordinal).Length;
        if (Text.Length - selectedLength + cleaned.Length > MaxDraftChars)
        {
            DraftInsertionRejected?.Invoke();
            return false;
        }
        base.OnPaste(cleaned.ToString());
        return true;
    }

    protected override bool OnPaste(string text)
    {
        TryInsertDraft(text);
        return true; // A refused paste must not bubble into another view.
    }

    protected override void OnUnwrappedCursorPositionChanged(Point position)
    {
        _sourceCursor = position;
        base.OnUnwrappedCursorPositionChanged(position);
    }
}
#pragma warning restore CS0618
