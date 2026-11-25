using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;

namespace RuAgent.Cli;

public sealed partial class AgentSlashCommands
{
    /// <summary>
    /// <c>/browser [keep|fresh]</c>: shows or saves whether the built-in browser keeps Ru's own
    /// persistent profile, and so its sign-ins, between sessions (ADR 0055 amendment 2026-11-25).
    /// It applies to new turns and never uses the user's own browser profile.
    /// </summary>
    private async Task HandleBrowserProfileAsync(string? arg, CancellationToken cancellationToken)
    {
        var choice = arg?.Trim().ToLowerInvariant();
        try
        {
            if (string.IsNullOrEmpty(choice))
            {
                var settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
                var keep = settings.TryGetProperty("browser_persistent_profile", out var value) && value.ValueKind == JsonValueKind.True;
                await _humanOut.WriteLineAsync(
                    keep
                        ? "browser: keep · Ru's own browser profile keeps sign-ins between sessions · applies to new turns"
                        : "browser: fresh · every browser session starts with no cookies · applies to new turns")
                    .ConfigureAwait(false);
            }
            else if (choice is "keep" or "fresh")
            {
                await _settingsClient.UpdateAsync(new JsonObject { ["browser_persistent_profile"] = choice == "keep" }, cancellationToken)
                    .ConfigureAwait(false);
                await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ browser: {choice} for new turns")).ConfigureAwait(false);
            }
            else
            {
                await _err.WriteLineAsync("usage: /browser [keep|fresh]").ConfigureAwait(false);
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
