namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgChatWindow
{
    private TgQuestionPanel? _questionPanel;
    internal TgQuestionPanel QuestionPanel => _questionPanel ?? throw new InvalidOperationException("no question panel");

    internal void ShowQuestion(ChatInputQuestion question) => ShowQuestionPanel(panel => panel.SetQuestion(question));

    internal void ShowTextPrompt(string title, string description) =>
        ShowQuestionPanel(panel => panel.SetTextPrompt(title, description));

    private void ShowQuestionPanel(Action<TgQuestionPanel> configure)
    {
        Transcript.ClearTextSelection();
        HideDocument();
        HidePanel();
        HideSlashDropdown();
        _modalInputState = new ModalInputState(Composer.Input.Text, Composer.Input.InsertionPoint);
        _questionPanel = new TgQuestionPanel();
        configure(_questionPanel);
        _questionPanel.Answered += ResolveConfirmed;
        _questionPanel.Deferred += ResolveCancelled;
        TgModalLayout.Apply(_questionPanel, this, measureHeight: _questionPanel.MeasureHeight);
        Add(_questionPanel);
        SetNeedsDraw();
        _questionPanel.Editor.SetFocus();
    }
}
