using Spectre.Console;

namespace RuAgent.Cli.Tui;

public static class GrokHeader
{
    private const int MinConsoleWidth = 10;
    private const int MinPanelWidth = 20;
    private const int WideShortcutsWidth = 105;
    private const int MediumShortcutsWidth = 75;
    private const int MaxDividerWidth = 1000;

    public static void Render(IAnsiConsole console, string sessionId, string workspace, string backend, string model, string preset)
    {
        ArgumentNullException.ThrowIfNull(console);
        if (!AnsiTheme.IsEnabled)
        {
            console.WriteLine($"RuAgent · Session: {sessionId} · Workspace: {workspace} · {backend}/{model} · {preset}");
            return;
        }

        int width = console.Profile.Width;
        try
        {
            if (!Console.IsOutputRedirected && Console.WindowWidth > MinConsoleWidth)
            {
                width = Math.Max(MinPanelWidth, Console.WindowWidth - 2);
                console.Profile.Width = width;
            }
        }
        catch (IOException)
        {
            // No live console; keep the profile width Spectre was created with.
        }

        string shortcuts;
        if (width >= WideShortcutsWidth)
        {
            shortcuts =
                $"{MenuRow("/model", "switch model")}  {MenuRow("/fork", "branch session")}  {MenuRow("/stats", "usage stats")}\n" +
                $"{MenuRow("/clear", "clear screen")}  {MenuRow("/help", "all commands")}    {MenuRow("/exit", "quit")}";
        }
        else if (width >= MediumShortcutsWidth)
        {
            shortcuts =
                $"{MenuRow("/model", "switch model")}  {MenuRow("/fork", "branch session")}\n" +
                $"{MenuRow("/stats", "usage stats")}   {MenuRow("/exit", "quit")}\n" +
                $"{MenuRow("/clear", "clear screen")}  {MenuRow("/help", "all commands")}";
        }
        else
        {
            shortcuts =
                $"{MenuRow("/model", "switch model")}\n" +
                $"{MenuRow("/fork", "branch session")}\n" +
                $"{MenuRow("/stats", "usage stats")}\n" +
                $"{MenuRow("/clear", "clear screen")}\n" +
                $"{MenuRow("/exit", "quit")}";
        }

        int dividerWidth = Math.Clamp(width - 6, MinPanelWidth, MaxDividerWidth);
        var divider = new string('─', dividerWidth);

        var infoText =
            $"[bold cyan]✦ RuAgent v2.0[/] [grey]· Autonomous Coding Agent[/]\n" +
            $"[teal]{divider}[/]\n" +
            $"[grey]Session:[/]   [bold white]{Markup.Escape(ShortId(sessionId))}[/]\n" +
            $"[grey]Workspace:[/] [silver]{Markup.Escape(workspace)}[/]\n" +
            $"[grey]Engine:[/]    [bold yellow]{Markup.Escape(model)}[/] [grey]({Markup.Escape(backend)} · {Markup.Escape(preset)})[/]\n" +
            $"\n" +
            $"{shortcuts}\n" +
            $"\n" +
            $"[grey]Type [/][bold cyan]/[/][grey] to view & auto-complete available commands[/]\n" +
            $"[grey](↑/↓ to choose, Tab to apply)[/]";

        var panel = new Panel(new Markup(infoText))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Teal),
            Expand = true,
            Width = width,
        };

        console.Write(panel);
    }

    private static string MenuRow(string command, string hint) =>
        $"[grey][[ [/][bold white]{command.PadRight(10)}[/][grey]{hint} ]][/]";

    private static string ShortId(string sessionId) =>
        string.IsNullOrEmpty(sessionId) || sessionId.Length <= 10 ? sessionId : sessionId[..8] + "…";
}
