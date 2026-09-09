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
            $"[bold]RuAgent[/] [dim]v2.0 · Autonomous Coding Agent[/]\n" +
            $"[dim]Session:[/] [yellow]{Markup.Escape(sessionId)}[/]\n" +
            $"[dim]Workspace:[/] {Markup.Escape(workspace)}\n" +
            $"[dim]Backend · Model:[/] {Markup.Escape(backend)} · {Markup.Escape(model)} [dim]({Markup.Escape(preset)})[/]\n" +
            $"[dim]/model /fork /rewind /stats /export /history /exit[/]");
        console.Write(new Panel(body) { Header = new PanelHeader(" [cyan]❯ RuAgent[/] "), Border = BoxBorder.Rounded, Expand = true });
    }
}
