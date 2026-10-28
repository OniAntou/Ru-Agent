using RuAgent.Cli.Images;
using RuAgent.Cli.Tui;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>A visible answer editor; suggestions fill it, and only explicit Enter sends it.</summary>
internal sealed class TgQuestionPanel : View
{
    private string _question = "";
    private IReadOnlyList<string> _options = [];
    private IReadOnlyList<string> _questionRows = [];
    private int _wrapWidth = -1;
    private int _offset;
    private int _selected = -1;
    private string? _error;
    internal TgPromptEditor Editor { get; } = new();
    internal event Action<string>? Answered;
    internal event Action? Deferred;

    internal TgQuestionPanel()
    {
        CanFocus = true;
        SetScheme(TgPalette.Scheme(TuiToken.Ink, TgPalette.CardBgRgb()));
        Editor.SetScheme(TgPalette.Scheme(TuiToken.Ink, TuiPalette.SurfaceRgb()));
        Editor.X = 2;
        Editor.Y = Pos.Func(_ => EditorTop);
        Editor.Width = Dim.Fill(2);
        Editor.Height = Dim.Func(_ => InputRows);
        Editor.KeyDown += OnEditorKey;
        Add(Editor);
    }

    private int InputRows => Math.Clamp(Viewport.Height / 5, 1, 3);
    private int OptionRows => Math.Min(_options.Count, Math.Max(0, Math.Min(3, Viewport.Height - 8)));
    private int QuestionRows => Math.Max(1, Math.Min(6, Viewport.Height - InputRows - OptionRows - 5));
    private int EditorTop => 3 + QuestionRows + OptionRows;

    internal void SetQuestion(ChatInputQuestion question)
    {
        _question = TuiText.SanitizeUntrusted(question.Question);
        _options = question.Options.Select(TuiText.SanitizeUntrusted).ToArray();
        _wrapWidth = -1;
        _offset = 0;
        _selected = -1;
        _error = null;
        Editor.Text = "";
        SetNeedsLayout();
        SetNeedsDraw();
    }

    internal int MeasureHeight(int width, int available)
    {
        EnsureQuestionRows(width);
        return Math.Min(available, Math.Min(6, _questionRows.Count) + Math.Min(3, _options.Count) + 8);
    }

    private void EnsureQuestionRows(int width)
    {
        var inner = Math.Max(1, width - 4);
        if (_wrapWidth == inner) return;
        _questionRows = TuiText.HardWrap(_question.Split('\n'), inner);
        _wrapWidth = inner;
    }

    internal bool Paste(string text)
    {
        var cleaned = TuiText.SanitizeUntrusted(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        if (Editor.Text.Length - Editor.SelectedText.Length + cleaned.Length > ChatInputWorkflow.MaxAnswerCharacters)
        {
            _error = "Answer limit: 8192 characters";
            SetNeedsDraw();
            return false;
        }
        return Editor.TryInsertDraft(cleaned);
    }

    private void OnEditorKey(object? sender, Key key)
    {
        if (key == Key.Esc) { key.Handled = true; Deferred?.Invoke(); }
        else if (key == Key.J.WithCtrl || key == Key.Enter.WithShift)
        { key.Handled = true; Paste("\n"); }
        else if (key == Key.Enter)
        {
            key.Handled = true;
            if (ChatInputWorkflow.IsValidAnswer(Editor.Text)) Answered?.Invoke(Editor.Text);
            else { _error = "Enter an answer of 1..8192 characters"; SetNeedsDraw(); }
        }
        else if (key == Key.Tab || key == Key.Tab.WithShift)
        {
            key.Handled = true;
            if (_options.Count == 0) return;
            _selected = _selected < 0 ? (key == Key.Tab ? 0 : _options.Count - 1)
                : (_selected + (key == Key.Tab ? 1 : _options.Count - 1)) % _options.Count;
            Editor.Text = _options[_selected];
            Editor.InsertionPoint = Editor.Text.Length;
            _error = null;
            SetNeedsDraw();
        }
        else if (key == Key.PageUp || key == Key.PageDown)
        {
            key.Handled = true;
            EnsureQuestionRows(Viewport.Width);
            _offset = Math.Clamp(_offset + (key == Key.PageDown ? QuestionRows : -QuestionRows), 0,
                Math.Max(0, _questionRows.Count - QuestionRows));
            SetNeedsDraw();
        }
        else if (key == Key.V.WithCtrl)
        { key.Handled = true; Paste(CliClipboard.TryReadText() ?? ""); }
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width < 1 || Viewport.Height < 1) return true;
        EnsureQuestionRows(width);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _questionRows.Count - QuestionRows));
        for (var row = 0; row < Viewport.Height; row++) Paint(row, "", TuiToken.Ink);
        Paint(0, "Question · answer in Ru", TuiToken.Brand2);
        for (var index = 0; index < QuestionRows && _offset + index < _questionRows.Count; index++)
            Paint(2 + index, _questionRows[_offset + index], TuiToken.Ink);
        var start = _selected < 0 ? 0 : _selected / Math.Max(1, OptionRows) * Math.Max(1, OptionRows);
        for (var index = 0; index < OptionRows && start + index < _options.Count; index++)
        {
            var option = start + index;
            Paint(2 + QuestionRows + index, $"{(option == _selected ? '›' : ' ')} {option + 1}. {_options[option]}",
                option == _selected ? TuiToken.Ok : TuiToken.Meta);
        }
        Paint(EditorTop - 1, _error ?? "Answer (Tab cycles suggestions)", _error is null ? TuiToken.Meta : TuiToken.Err);
        Paint(Viewport.Height - 1, "enter send · tab option · ctrl+j newline · pgup/dn question · esc later", TuiToken.Meta);
        return true;
    }

    private void Paint(int row, string text, TuiToken tone)
    {
        if (row < 0 || row >= Viewport.Height) return;
        Move(0, row);
        SetAttribute(TgPalette.Attr(tone, TgPalette.CardBgRgb()));
        AddStr("  " + TuiText.Pad(TuiText.Truncate(text, Math.Max(1, Viewport.Width - 4)), Math.Max(1, Viewport.Width - 4)) + "  ");
    }
}
