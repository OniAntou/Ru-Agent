using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;

namespace RuAgent.Cli;

public sealed partial class AgentSlashCommands
{
    /// <summary>
    /// <c>/computer [on|off]</c>: shows or saves the Host's computer-use opt-in (ADR 0031).
    /// It applies to new turns with an image-capable model; Ctrl+Alt+Backspace (or Ctrl+Alt+Pause) stops it.
    /// </summary>
    private async Task HandleComputerUseAsync(string? arg, CancellationToken cancellationToken)
    {
        var choice = arg?.Trim().ToLowerInvariant();
        try
        {
            if (string.IsNullOrEmpty(choice))
            {
                var settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
                var enabled = settings.TryGetProperty("computer_use_enabled", out var value) && value.ValueKind == JsonValueKind.True;
                await _humanOut.WriteLineAsync(
                    $"computer use: {(enabled ? "on" : "off")} · applies to new turns with an image-capable model · stop with Ctrl+Alt+Backspace (or Ctrl+Alt+Pause)")
                    .ConfigureAwait(false);
            }
            else if (choice is "on" or "off")
            {
                await _settingsClient.UpdateAsync(new JsonObject { ["computer_use_enabled"] = choice == "on" }, cancellationToken)
                    .ConfigureAwait(false);
                await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ computer use: {choice} for new turns")).ConfigureAwait(false);
            }
            else
            {
                await _err.WriteLineAsync("usage: /computer [on|off]").ConfigureAwait(false);
                await _err.FlushAsync().ConfigureAwait(false);
                return;
            }

            await _humanOut.FlushAsync().ConfigureAwait(false);
        }
        catch (AgentApiException error)
        {
            await _err.WriteLineAsync(CliErrorText.Format(error)).ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
        }
    }
}
