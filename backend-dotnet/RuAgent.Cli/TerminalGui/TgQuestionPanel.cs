using RuAgent.Cli.Images;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Scrollable answer choices above an editor that submits only on Enter.</summary>
internal sealed class TgQuestionPanel : View
{
    private const int BodyTop = 2;
    private const int ChromeRows = BodyTop + 2; // Answer label and footer.
    private const int MaxEditorRows = 3;
    private const string OtherLabel = "Other · write your own answer";

    private readonly record struct BodyRow(string Text, int Choice = -1, bool First = false);

    private string _question = "";
    private string? _inputTitle;
    private string? _originalDocument;
    private bool _confirmDiscard;
    private IReadOnlyList<string> _options = [];
    private readonly List<BodyRow> _body = [];
    private readonly List<int> _choiceStarts = [];
    private int _wrapWidth = -1;
    private int _offset;
    private int _selected = -1;
    private string _customDraft = "";
    private int _customCursor;
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
        Editor.ContentsChanged += (_, _) =>
        {
            _error = null;
            SetNeedsDraw();
        };
        Add(Editor);
    }

    private int InputRows => _originalDocument is null
        ? Math.Clamp(Viewport.Height - ChromeRows - 1, 1, MaxEditorRows)
        : Math.Max(1, Viewport.Height - ChromeRows - 2);
    private int BodyHeight => Math.Max(0, Viewport.Height - ChromeRows - InputRows);
    private int EditorTop => BodyTop + BodyHeight + 1;
    private int ChoiceCount => _options.Count == 0 ? 0 : _options.Count + 1;
    private bool IsPreset(int choice) => choice >= 0 && choice < _options.Count;
    // Editing a suggested answer turns it into a custom draft.
    private int SelectedChoice => IsPreset(_selected) && Editor.Text != _options[_selected] ? _options.Count : _selected;

    internal void SetQuestion(ChatInputQuestion question) => SetPrompt(null, question.Question, question.Options);

    internal void SetTextPrompt(string title, string description) => SetPrompt(title, description, []);

    internal void SetDocumentEditor(string title, string description, string original, string draft)
    {
        SetPrompt(title, description, []);
        _originalDocument = InstructionFileStore.NormalizeNewlines(original);
        Editor.Text = draft;
        Editor.MaxPasteCharacters = InstructionFileStore.MaxBytes;
        Editor.TabKeyAddsTab = true;
        Editor.DraftInsertionRejected += () =>
        {
            _error = "Instruction file limit: 64 KiB";
            SetNeedsDraw();
        };
    }

    private void SetPrompt(string? title, string question, IReadOnlyList<string> options)
    {
        _inputTitle = title is null ? null : TuiText.SanitizeUntrusted(title);
        _question = TuiText.SanitizeUntrusted(question);
        _options = options.Select(TuiText.SanitizeUntrusted).ToArray();
        _wrapWidth = -1;
        _offset = 0;
        _selected = -1;
        _customDraft = "";
        _customCursor = 0;
        _error = null;
        Editor.Text = "";
        SetNeedsLayout();
        SetNeedsDraw();
    }

    internal int MeasureHeight(int width, int available)
    {
        if (_originalDocument is not null) return available;
        EnsureBodyRows(width);
        return Math.Min(available, _body.Count + ChromeRows + MaxEditorRows);
    }

    private void EnsureBodyRows(int width)
    {
        var inner = Math.Max(1, width - 4);
        if (_wrapWidth == inner) return;
        _wrapWidth = inner;
        _body.Clear();
        _choiceStarts.Clear();
        foreach (var line in TuiText.Wrap(TuiText.ExpandTabs(_question), inner)) _body.Add(new(line));
        if (ChoiceCount == 0) return;
        _body.Add(new(""));
        for (var choice = 0; choice < ChoiceCount; choice++)
        {
            _choiceStarts.Add(_body.Count);
            var label = IsPreset(choice) ? _options[choice] : OtherLabel;
            var number = $"{choice + 1}. ";
            var prefixWidth = number.Length + 2;
            var lines = TuiText.Wrap(TuiText.ExpandTabs(label), Math.Max(1, inner - prefixWidth));
            _body.Add(new(number + (lines.Count > 0 ? lines[0] : ""), choice, First: true));
            foreach (var line in lines.Skip(1)) _body.Add(new(new string(' ', prefixWidth) + line, choice));
        }
    }

    private void ScrollBody(int rows)
    {
        EnsureBodyRows(Viewport.Width);
        _offset = Math.Clamp(_offset + rows, 0, Math.Max(0, _body.Count - BodyHeight));
        SetNeedsDraw();
    }

    private void MoveChoice(int step)
    {
        var current = SelectedChoice;
        var next = current < 0 ? (step > 0 ? 0 : ChoiceCount - 1) : (current + step + ChoiceCount) % ChoiceCount;
        SelectChoice(next, reveal: true);
    }

    private void SelectChoice(int choice, bool reveal)
    {
        var current = SelectedChoice;
        if (current != choice)
        {
            if (!IsPreset(current))
            {
                _customDraft = Editor.Text;
                _customCursor = Editor.InsertionPoint;
            }
            _selected = choice;
            Editor.Text = IsPreset(choice) ? _options[choice] : _customDraft;
            Editor.InsertionPoint = IsPreset(choice) ? Editor.Text.Length : _customCursor;
            _error = null;
        }
        if (reveal)
        {
            EnsureBodyRows(Viewport.Width);
            var start = _choiceStarts[choice];
            var end = choice + 1 < ChoiceCount ? _choiceStarts[choice + 1] : _body.Count;
            _offset = end - start > BodyHeight ? start : Math.Clamp(_offset, Math.Max(0, end - BodyHeight), start);
        }
        Editor.SetFocus();
        SetNeedsDraw();
    }

    internal bool Paste(string text)
    {
        var cleaned = TuiText.SanitizeUntrusted(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));
        if (_originalDocument is not null) return !_confirmDiscard && Editor.TryInsertDraft(cleaned);
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
        if (_originalDocument is not null)
        {
            HandleDocumentKey(key);
            return;
        }
        if (key == Key.Esc) { key.Handled = true; Deferred?.Invoke(); }
        else if (key == Key.J.WithCtrl || key == Key.Enter.WithShift)
        { key.Handled = true; Paste("\n"); }
        else if (key == Key.Enter)
        {
            key.Handled = true;
            if (_inputTitle is not null && Editor.Text.Contains('\n'))
            {
                _error = "Enter a single line";
                SetNeedsDraw();
            }
            else if (ChatInputWorkflow.IsValidAnswer(Editor.Text)) Answered?.Invoke(Editor.Text);
            else { _error = _inputTitle is null ? "Enter an answer of 1..8192 characters" : "Enter 1..8192 characters"; SetNeedsDraw(); }
        }
        else if (key == Key.Tab || key == Key.Tab.WithShift)
        {
            key.Handled = true;
            if (ChoiceCount > 0) MoveChoice(key == Key.Tab ? 1 : -1);
        }
        else if (ChoiceCount > 0 && (key == Key.CursorUp && (IsPreset(SelectedChoice) || Editor.IsAtFirstVisualRow)
            || key == Key.CursorDown && (IsPreset(SelectedChoice) || Editor.IsAtLastVisualRow)))
        {
            key.Handled = true;
            MoveChoice(key == Key.CursorUp ? -1 : 1);
        }
        else if (key == Key.PageUp || key == Key.PageDown)
        {
            key.Handled = true;
            ScrollBody(key == Key.PageDown ? BodyHeight : -BodyHeight);
        }
        else if (key == Key.V.WithCtrl)
        { key.Handled = true; Paste(CliClipboard.TryReadText() ?? ""); }
    }

    private void HandleDocumentKey(Key key)
    {
        if (key == Key.S.WithCtrl)
        {
            key.Handled = true;
            _confirmDiscard = false;
            var text = Editor.Text;
            if (InstructionFileStore.ByteCount(text) <= InstructionFileStore.MaxBytes)
                Answered?.Invoke(text);
            else
            {
                _error = "Instruction file limit: 64 KiB";
                SetNeedsDraw();
            }
        }
        else if (key == Key.Esc)
        {
            key.Handled = true;
            if (_confirmDiscard)
                _confirmDiscard = false;
            else if (_originalDocument == Editor.Text)
            {
                Deferred?.Invoke();
                return;
            }
            else
                _confirmDiscard = true;
            SetNeedsDraw();
        }
        else if (_confirmDiscard)
        {
            key.Handled = true;
            if (key == Key.D) Deferred?.Invoke();
        }
        else if (key == Key.V.WithCtrl)
        {
            key.Handled = true;
            Paste(CliClipboard.TryReadText() ?? "");
        }
    }

    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Position is not { } position || position.Y < BodyTop || position.Y >= BodyTop + BodyHeight)
            return false;
        if (mouse.Flags.HasFlag(MouseFlags.WheeledUp)) ScrollBody(-3);
        else if (mouse.Flags.HasFlag(MouseFlags.WheeledDown)) ScrollBody(3);
        else if (mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked))
        {
            EnsureBodyRows(Viewport.Width);
            var row = _offset + position.Y - BodyTop;
            if (row >= _body.Count || _body[row].Choice < 0) return false;
            SelectChoice(_body[row].Choice, reveal: false);
        }
        else return false;
        mouse.Handled = true;
        return true;
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width < 1 || Viewport.Height < 1) return true;
        EnsureBodyRows(width);
        _offset = Math.Clamp(_offset, 0, Math.Max(0, _body.Count - BodyHeight));
        for (var row = 0; row < Viewport.Height; row++) Paint(row, "", TuiToken.Ink);
        Paint(0, _inputTitle ?? "Question · answer in Ru", TuiToken.Brand2);
        var selected = SelectedChoice;
        for (var index = 0; index < BodyHeight && _offset + index < _body.Count; index++)
        {
            var row = _body[_offset + index];
            var active = row.Choice >= 0 && row.Choice == selected;
            var text = row.First ? (active ? "› " : "  ") + row.Text : row.Text;
            Paint(BodyTop + index, text, row.Choice < 0 ? TuiToken.Ink : active ? TuiToken.Ok : TuiToken.Meta);
        }
        var label = _originalDocument is not null ? "Markdown" : _inputTitle is null ? "Answer" : "Value";
        Paint(EditorTop - 1, _error ?? label, _error is null ? TuiToken.Meta : TuiToken.Err);
        var keys = _originalDocument is not null
            ? _confirmDiscard ? "Unsaved · Ctrl+S save · D discard · Esc keep editing" : "Ctrl+S save · Esc back"
            : _inputTitle is not null ? "Enter confirm · Esc cancel"
            : ChoiceCount > 0 ? "↑/↓ or click select · Enter send · Esc later" : "Enter send · Ctrl+J newline · Esc later";
        if (_body.Count > BodyHeight) keys += " · PgUp/Dn scroll";
        Paint(Viewport.Height - 1, keys, TuiToken.Meta);
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
