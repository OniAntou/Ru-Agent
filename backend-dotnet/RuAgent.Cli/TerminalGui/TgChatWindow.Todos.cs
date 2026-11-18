using RuAgent.Protocol.Harness;

namespace RuAgent.Cli.TerminalGui;

public sealed partial class TgChatWindow
{
    private bool _todosDocumentOpen;

    internal void SetTodos(IReadOnlyList<AgentTodoWire> items)
    {
        var hadItems = Todos.HasItems;
        var wasCompleted = Todos.AllCompleted;
        if (!Todos.SetItems(items)) return;
        if (hadItems != Todos.HasItems || wasCompleted != Todos.AllCompleted) Relayout();
        if (!_todosDocumentOpen) return;
        if (!Todos.HasItems) HideDocument();
        else Document.RefreshContent(Todos.ProgressLabel, Todos.DocumentRows());
    }

    internal bool ToggleTodos()
    {
        if (_todosDocumentOpen)
        {
            HideDocument();
            return true;
        }
        if (!Todos.Visible || HasBlockingOverlay || IsSlashDropdownOpen) return false;
        ShowDocument(Todos.ProgressLabel, Todos.DocumentRows());
        _todosDocumentOpen = true;
        return true;
    }
}
