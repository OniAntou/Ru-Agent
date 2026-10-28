namespace RuApp.Services;

/// <summary>
/// The destination of one shell navigation tag. Code-behind maps this to a view
/// instance; the decision itself lives here so it can be tested without a window.
/// </summary>
public enum ShellDestination
{
    Chat,
    Log,
    Diagnostics,
    Policy,
    Audit,
    Customize,
}

/// <summary>
/// Routing decisions for the shell's side panel and tools menu. Keeping them out of
/// the code-behind makes "which tag goes where" and "which view is active" assertable
/// behaviour instead of a string match against MainWindow.xaml.cs.
/// </summary>
public static class ShellRoutePolicy
{
    public static ShellDestination Resolve(string? tag) => tag switch
    {
        "log" => ShellDestination.Log,
        "diagnostics" => ShellDestination.Diagnostics,
        "policy" => ShellDestination.Policy,
        "audit" => ShellDestination.Audit,
        "customize" => ShellDestination.Customize,
        // An unknown or absent tag is the chat surface: navigation never leaves the
        // user on a blank frame.
        _ => ShellDestination.Chat,
    };

    public static bool IsDiagnosticsActive(string? tag) =>
        Resolve(tag) == ShellDestination.Diagnostics;

    public static bool IsLogActive(string? tag) =>
        Resolve(tag) == ShellDestination.Log;

    public static bool IsCustomizeActive(string? tag) =>
        Resolve(tag) == ShellDestination.Customize;

    /// <summary>
    /// Live provider-catalog polling only belongs to the chat surface: the picker
    /// that consumes it is not visible anywhere else.
    /// </summary>
    public static bool KeepsCatalogPolling(string? tag) =>
        Resolve(tag) == ShellDestination.Chat;
}
