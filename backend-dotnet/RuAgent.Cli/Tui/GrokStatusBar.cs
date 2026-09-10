using Spectre.Console;

namespace RuAgent.Cli.Tui;

public static class GrokStatusBar
{
    public static void Render(IAnsiConsole console, string workspace, string preset, int turn, long? totalTokens)
    {
        ArgumentNullException.ThrowIfNull(console);
        var plain = $"{workspace} · {preset} · turn {turn}";
        if (!AnsiTheme.IsEnabled)
        {
            console.WriteLine(plain + (totalTokens is { } t ? $" · ⇣{t}" : string.Empty));
            return;
        }

        console.MarkupLine($"[grey]{Markup.Escape(workspace)} · {Markup.Escape(preset)} · turn {turn}[/]{(totalTokens is { } tt ? $" [yellow]⇣{tt}[/]" : string.Empty)}");
    }
}
