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
                await WriteProviderListNoticeAsync(providers, error).ConfigureAwait(false);
                var choices = TuiSurfaces.BackendChoices(ReadBackend(settings), states, providers);
                if (Picker is null)
                {
                    foreach (var line in TuiPanel.Compose(PanelWidth, "provider",
                        [new PanelSection(null, choices.Select(item => new PanelRow(item.Label, item.Detail)).ToList())],
                        "/provider <name> · select provider, then enter API key"))
                        await _humanOut.WriteLineAsync(line).ConfigureAwait(false);
                    return;
                }
                selected = await Picker(new ChoiceList("provider · choose before entering API key", choices), ct).ConfigureAwait(false);
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
            if (secret.Length > 0)
                foreach (var (field, value) in ProviderCredential.KeyPayload(provider, secret)) body[field] = value;
            // One request after confirmation: no early provider switch, no automatic POST retry.
            await _settingsClient.UpdateAsync(body, ct).ConfigureAwait(false);
            await _humanOut.WriteLineAsync(TuiPalette.Ok($"✓ Provider selected: {provider.DisplayName}")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Host/transport errors can contain submitted content. Never echo them after a secret prompt.
            await _err.WriteLineAsync("provider_setup_failed: could not confirm settings; check /status before trying again.").ConfigureAwait(false);
        }
    }
}
