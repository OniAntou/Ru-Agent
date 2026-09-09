using Spectre.Console;

namespace RuAgent.Cli.Tui;

public static class GrokHeader
{
    public static void Render(IAnsiConsole console, string sessionId, string workspace, string backend, string model, string preset)
    {
        ArgumentNullException.ThrowIfNull(console);
        if (!AnsiTheme.IsEnabled)
        {
            console.WriteLine($"RuAgent · Session: {sessionId} · Workspace: {workspace} · {backend}/{model} · {preset}");
            return;
        }

        console.WriteLine(AnsiTheme.White(AnsiTheme.Bold("RuAgent")) + AnsiTheme.Gray(" v2.0 · Autonomous Coding Agent"));
        console.WriteLine(AnsiTheme.Gray("Session:   ") + AnsiTheme.Silver(sessionId));
        console.WriteLine(AnsiTheme.Gray("Workspace: ") + AnsiTheme.Silver(workspace));
        console.WriteLine(AnsiTheme.Gray($"{backend} · {model} ({preset})"));
        console.WriteLine(AnsiTheme.Dim("/model /fork /rewind /stats /export /history /exit"));
    }
}
