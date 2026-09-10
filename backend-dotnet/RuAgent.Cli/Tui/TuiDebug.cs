using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>
/// Opt-in diagnostics for layout problems that only reproduce on a live terminal. Enabled by
/// setting <c>RU_TUI_DEBUG</c> to a file path (or any value, which uses a file next to the
/// user's temp directory). Disabled — and allocation-free — when the variable is absent.
/// </summary>
public static class TuiDebug
{
    private static readonly bool Enabled;
    private static readonly string? Path;
    private static readonly object Gate = new();

    static TuiDebug()
    {
        var value = Environment.GetEnvironmentVariable("RU_TUI_DEBUG");
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        Enabled = true;
        Path = value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-tui-debug.log")
            : value;
    }

    public static bool IsEnabled => Enabled;

    public static void Log(string area, string message)
    {
        if (!Enabled || Path is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    Path,
                    $"{DateTime.Now:HH:mm:ss.fff} {area} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Diagnostics must never break the UI.
        }
        catch (UnauthorizedAccessException)
        {
            // Diagnostics must never break the UI.
        }
    }
}
