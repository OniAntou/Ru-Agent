using Spectre.Console;

namespace RuAgent.Cli.Tui;

public static class GrokStatusBar
{
    public static void Render(IAnsiConsole console, string workspace, string backend, string model, string preset, int turn, long? totalTokens)
    {
        ArgumentNullException.ThrowIfNull(console);
        if (!AnsiTheme.IsEnabled)
        {
            var tokens = totalTokens is { } t ? $" · ⇣{t}" : string.Empty;
            console.WriteLine($"{workspace} · {backend}/{model} · {preset} · turn {turn}{tokens}");
            return;
        }

        console.Write(new Rule($"[dim]{Markup.Escape(workspace)} · {Markup.Escape(backend)}/{Markup.Escape(model)} · {Markup.Escape(preset)} · turn {turn}[/]{(totalTokens is { } tt ? $" [yellow]⇣{tt}[/]" : string.Empty)}"));
    }
}
