using RuAgent.Protocol;

namespace RuAgent.Broker.Policy;

public static class ToolScope
{
    public const string SystemRead = AgentToolScopes.SystemRead;
    public const string WorkspaceRead = AgentToolScopes.WorkspaceRead;
    public const string WorkspaceWrite = AgentToolScopes.WorkspaceWrite;
    public const string ClipboardRead = "clipboard.read";
    public const string ClipboardWrite = "clipboard.write";
    public const string TerminalExecute = AgentToolScopes.TerminalExecute;
    public const string TerminalNetwork = "terminal.network";
    public const string WebSearch = AgentToolScopes.NetworkSearch;
    public const string WebFetch = AgentToolScopes.NetworkFetch;
    public const string CalendarWrite = "calendar.write";
    public const string TasksWrite = "tasks.write";
    public const string MailRead = "mail.read";
    public const string MailDraft = "mail.draft";
    public const string MailSend = "mail.send";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(
        [
            SystemRead,
            WorkspaceRead,
            WorkspaceWrite,
            ClipboardRead,
            ClipboardWrite,
            TerminalExecute,
            TerminalNetwork,
            WebSearch,
            WebFetch,
            CalendarWrite,
            TasksWrite,
            MailRead,
            MailDraft,
            MailSend,
        ],
        StringComparer.Ordinal);
}
