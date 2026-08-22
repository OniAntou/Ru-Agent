namespace RuAgent.Broker.Policy;

public static class ToolScope
{
    public const string SystemRead = "system.read";
    public const string SystemWrite = "system.write";
    public const string AppOpen = "app.open";
    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceWrite = "workspace.write";
    public const string ClipboardRead = "clipboard.read";
    public const string ClipboardWrite = "clipboard.write";
    public const string TerminalExecute = "terminal.execute";
    public const string TerminalNetwork = "terminal.network";
    public const string WebSearch = "web.search";
    public const string WebFetch = "web.fetch";
    public const string SkillExecute = "skill.execute";
    public const string CalendarWrite = "calendar.write";
    public const string TasksWrite = "tasks.write";
    public const string MailRead = "mail.read";
    public const string MailDraft = "mail.draft";
    public const string MailSend = "mail.send";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(
        [
            SystemRead,
            SystemWrite,
            AppOpen,
            WorkspaceRead,
            WorkspaceWrite,
            ClipboardRead,
            ClipboardWrite,
            TerminalExecute,
            TerminalNetwork,
            WebSearch,
            WebFetch,
            SkillExecute,
            CalendarWrite,
            TasksWrite,
            MailRead,
            MailDraft,
            MailSend,
        ],
        StringComparer.Ordinal);
}
