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

        var body = new Markup(
            $"[bold cyan]✦ RuAgent v2.0[/] [grey]· Autonomous Coding Agent[/]\n" +
            $"[grey]Session:[/] [bold white]{Markup.Escape(ShortId(sessionId))}[/] [grey]· Workspace:[/] [silver]{Markup.Escape(workspace)}[/]\n" +
            $"[grey]Engine:[/] [bold yellow]{Markup.Escape(model)}[/] [grey]({Markup.Escape(backend)} · {Markup.Escape(preset)})[/]\n" +
            $"\n" +
            $"{MenuRow("/model", "switch model")}  {MenuRow("/fork", "branch session")}\n" +
            $"{MenuRow("/stats", "usage stats")}   {MenuRow("/exit", "quit")}\n" +
            $"\n" +
            $"[grey]Type [/][bold cyan]/[/][grey] to view & auto-complete available commands (↑/↓ to choose, Tab to apply)[/]");
        console.Write(new Panel(body)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Teal),
            Expand = true,
        });
    }

    private static string MenuRow(string command, string hint) =>
        $"[grey][[ [/][bold white]{command.PadRight(10)}[/][grey]{hint} ]][/]";

    private static string ShortId(string sessionId) =>
        string.IsNullOrEmpty(sessionId) || sessionId.Length <= 10 ? sessionId : sessionId[..8] + "…";
}
