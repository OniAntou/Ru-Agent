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
            $"[bold white]RuAgent v2.0[/] [grey]· Autonomous Coding Agent[/]\n" +
            $"[grey]Session {Markup.Escape(ShortId(sessionId))} · {Markup.Escape(workspace)}[/]\n" +
            $"[bold yellow]{Markup.Escape(model)}[/][grey] ({Markup.Escape(preset)})[/]\n" +
            $"\n" +
            $"{MenuRow("/model", "switch model")}\n" +
            $"{MenuRow("/fork", "branch session")}\n" +
            $"{MenuRow("/stats", "usage stats")}\n" +
            $"{MenuRow("/exit", "quit")}");
        console.Write(new Panel(body)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Expand = true,
        });
    }

    private static string MenuRow(string command, string hint) =>
        $"[grey][[ [/][bold white]{command.PadRight(10)}[/][grey]{hint} ]][/]";

    private static string ShortId(string sessionId) =>
        string.IsNullOrEmpty(sessionId) || sessionId.Length <= 10 ? sessionId : sessionId[..8] + "…";
}
