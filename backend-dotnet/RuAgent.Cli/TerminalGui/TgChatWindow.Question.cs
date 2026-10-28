namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgChatWindow
{
    private TgQuestionPanel? _questionPanel;
    internal TgQuestionPanel QuestionPanel => _questionPanel ?? throw new InvalidOperationException("no question panel");

    internal void ShowQuestion(ChatInputQuestion question)
    {
        HideDocument();
        HidePanel();
        HideSlashDropdown();
        _modalInputState = new ModalInputState(Composer.Input.Text, Composer.Input.InsertionPoint);
        _questionPanel = new TgQuestionPanel();
        _questionPanel.SetQuestion(question);
        _questionPanel.Answered += ResolveConfirmed;
        _questionPanel.Deferred += ResolveCancelled;
        TgModalLayout.Apply(_questionPanel, this, measureHeight: _questionPanel.MeasureHeight);
        Add(_questionPanel);
        SetNeedsDraw();
        _questionPanel.Editor.SetFocus();
    }
}
