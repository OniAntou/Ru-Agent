using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private const string PreviousCustomizeTab = "tab:previous";
        private const string NextCustomizeTab = "tab:next";
        private static readonly string[] CustomizeTabs = ["Skills", "Plugins", "Instructions"];

        private async Task ShowCustomizeAsync(string? argument, CancellationToken ct)
        {
            if (!tgMode || tgWindow is null)
            {
                await WriteDiagnosticLineAsync(_err, "Customize requires an interactive terminal.").ConfigureAwait(false);
                return;
            }

            var tab = string.IsNullOrEmpty(argument) ? 0 : Array.FindIndex(CustomizeTabs,
                name => name.Equals(argument, StringComparison.OrdinalIgnoreCase));
            if (tab < 0)
            {
                await WriteDiagnosticLineAsync(_err, "Usage: /customize [skills|plugins|instructions]").ConfigureAwait(false);
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                var selection = await OpenCustomizeTabAsync(tab, ct).ConfigureAwait(false);
                if (selection == PreviousCustomizeTab)
                    tab = (tab + CustomizeTabs.Length - 1) % CustomizeTabs.Length;
                else if (selection == NextCustomizeTab)
                    tab = (tab + 1) % CustomizeTabs.Length;
                else
                    return;
            }
        }

        private Task<string?> OpenCustomizeTabAsync(int tab, CancellationToken ct) =>
            PickCustomizeAsync(tab, [new("No settings available", "", "", Enabled: false)], ct);

        private Task<string?> PickCustomizeAsync(int tab, IReadOnlyList<ChoiceItem> rows, CancellationToken ct)
        {
            var tabs = string.Join("  ", CustomizeTabs.Select((name, index) => index == tab ? $"[{name}]" : name));
            return TgPanelRequests.RequestChoiceAsync(tgWindow!,
                new ChoiceList("Customize  ·  " + tabs, rows, footer: "←/→ tab · ↑/↓ select · enter actions · esc close",
                    showCounter: false),
                ct, direction => direction < 0 ? PreviousCustomizeTab : NextCustomizeTab);
        }

    }
}
