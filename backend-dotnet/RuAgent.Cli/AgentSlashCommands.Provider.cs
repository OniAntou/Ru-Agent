using System.Text.Json.Nodes;
using RuAgent.Client;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

public sealed partial class AgentSlashCommands
{
    /// <summary>Collect a provider and masked credential before one existing settings update.</summary>
    private async Task HandleProviderAsync(string? argument, CancellationToken ct)
    {
        if (argument?.StartsWith("remove ", StringComparison.OrdinalIgnoreCase) == true)
        {
            await RemoveKeyAsync(argument[7..].Trim(), ct).ConfigureAwait(false);
            return;
        }
        if (Json)
        {
            await _err.WriteLineAsync("interactive_required: /provider needs a masked key prompt; run without --json.").ConfigureAwait(false);
            return;
        }
        try
        {
            var settings = await _settingsClient.GetAsync(ct).ConfigureAwait(false);
            var states = await ReadAllKeyStatesAsync(settings, ct).ConfigureAwait(false);
            var selected = argument?.Trim();
            if (string.IsNullOrEmpty(selected))
            {
                var (providers, error) = await ReadModelsDevProvidersAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var choices = TuiSurfaces.ProviderChoices(ReadBackend(settings), states, providers);
                // The minimal surface carries no provider count or row ratio; only a failed
                // catalog earns a footer, so the operator knows which list they are seeing.
                var unavailable = error is null ? null : "models.dev list unavailable - showing known providers";
                if (Picker is null)
                {
                    if (unavailable is not null)
                    {
                        await _humanOut.WriteLineAsync(TuiPalette.DimPaint("  " + unavailable)).ConfigureAwait(false);
                    }

                    foreach (var line in TuiPanel.Compose(PanelWidth, "provider",
                        [new PanelSection(null, choices.Select(item => new PanelRow(item.Label, item.Detail)).ToList())],
                        null))
                        await _humanOut.WriteLineAsync(line).ConfigureAwait(false);
                    return;
                }
                selected = await Picker(new ChoiceList("provider", choices,
                    footer: unavailable,
                    showCounter: false,
                    hideFooter: unavailable is null), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (selected is null) return;
                if (!choices.Any(item => item.Enabled && item.Value == selected))
                {
                    await _err.WriteLineAsync("invalid_provider: choose an available provider.").ConfigureAwait(false);
                    return;
                }
            }
            var provider = ProviderCredential.Resolve(selected);
            if (provider is null)
            {
                await _err.WriteLineAsync("invalid_provider: use a provider name from /provider.").ConfigureAwait(false);
                return;
            }
            if (SecretPanelPrompt is null && SecretPrompt is null)
            {
                await _err.WriteLineAsync("interactive_required: /provider needs a masked key prompt.").ConfigureAwait(false);
                return;
            }
            var canKeep = states.TryGetValue(provider.Choice, out var present) && present;
            var hint = canKeep ? " · Enter keeps saved key" : provider.KeyIsOptional ? " · key optional" : "";
            var secret = SecretPanelPrompt is not null
                ? await SecretPanelPrompt(provider.DisplayName + hint, ct).ConfigureAwait(false)
                : await SecretPrompt!($"API key for {provider.DisplayName}{hint} (esc cancels): ", ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (secret is null) return;
            secret = secret.Trim();
            if (secret.Length == 0 && !canKeep && !provider.KeyIsOptional)
            {
                await _err.WriteLineAsync("provider_key_required: enter an API key; provider unchanged.").ConfigureAwait(false);
                return;
            }
            var body = new JsonObject { ["backend"] = provider.Choice };
            // The key is checked and stored first; a refused key leaves both the saved key
            // and the selected provider as they were.
            if (secret.Length > 0 && !await StoreProviderKeyAsync(provider, secret, ct).ConfigureAwait(false)) return;
            // One selection request after confirmation: no early provider switch, no automatic POST retry.
            await _settingsClient.UpdateAsync(body, ct).ConfigureAwait(false);
            await _humanOut.WriteLineAsync(TuiPalette.Ok($"✓ Provider selected: {provider.DisplayName}")).ConfigureAwait(false);
            await _humanOut.FlushAsync().ConfigureAwait(false);
            // models.dev backends share one model field, so the previous provider's model is
            // usually absent here. Choose one now instead of leaving a model the new
            // provider cannot serve pinned in settings.
            await PromptModelForBackendAsync(provider.Choice, provider.DisplayName, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Host/transport errors can contain submitted content. Never echo them after a secret prompt.
            await _err.WriteLineAsync("provider_setup_failed: could not confirm settings; check the logs before trying again.").ConfigureAwait(false);
        }
    }
}

