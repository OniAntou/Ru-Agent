namespace RuApp.Services;

/// <summary>
/// The app's keyboard shortcuts, in one place. The shell's key handler, the command
/// palette's hints and the shortcuts sheet all read this list, so a shortcut cannot be
/// documented in one place and bound differently in another.
/// </summary>
public static class KeyboardShortcutCatalog
{
    public sealed record Shortcut(string Id, string Keys, string Description, string Group);

    public const string CommandPalette = "palette";
    public const string NewChat = "new-chat";
    public const string FocusComposer = "focus-composer";
    public const string OpenSettings = "settings";
    public const string ShowShortcuts = "shortcuts";
    public const string ToggleSidePanel = "side-panel";

    public static IReadOnlyList<Shortcut> All { get; } =
    [
        new(CommandPalette, "Ctrl+K", "Open the command palette", "General"),
        new(NewChat, "Ctrl+N", "Start a new chat", "General"),
        new(OpenSettings, "Ctrl+,", "Open Settings", "General"),
        new(ShowShortcuts, "Ctrl+/", "Show keyboard shortcuts", "General"),
        new(FocusComposer, "Ctrl+L", "Focus the message box", "Chat"),
        new(ToggleSidePanel, "Ctrl+J", "Show or hide the side panel", "Chat"),
        new("send", "Enter", "Send, or queue while a run is in hand", "Chat"),
        new("newline", "Shift+Enter", "New line in the message box", "Chat"),
        new("stop", "Esc", "Stop the run (an open approval or question takes it first)", "Chat"),
        new("paste-image", "Ctrl+V", "Paste an image or file into the message box", "Chat"),
        new("nav-chat", "Ctrl+1", "Go to Chat", "Navigation"),
        new("nav-settings", "Ctrl+2", "Go to Settings", "Navigation"),
        new("sidebar-width", "Ctrl+← / Ctrl+→", "Narrow or widen the sidebar", "Navigation"),
    ];

    public static string? KeysFor(string id) => All.FirstOrDefault(shortcut => shortcut.Id == id)?.Keys;
}
