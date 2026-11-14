using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;

namespace RuAgent.Cli;

public sealed partial class AgentSlashCommands
{
    private const string AdvisorUsage = "/advisor [off | <model> | <provider> <model>]";
    private const string AdvisorOffChoice = "off";

    /// <summary>
    /// <c>/advisor</c>: shows or saves the model the main model may consult (ADR 0047). The
    /// bare form picks a provider and then a model; <c>&lt;model&gt;</c> uses the active
    /// provider, <c>&lt;provider&gt; &lt;model&gt;</c> names one. It applies to new runs.
    /// </summary>
    private async Task HandleAdvisorAsync(string? arg, CancellationToken cancellationToken)
    {
        var choice = arg?.Trim() ?? string.Empty;
        if (Json && choice.Length == 0)
        {
            // The bare form is a picker; --json must not prompt or read stdin, and
            // picking for the user would update settings they never chose.
            await _err.WriteLineAsync($"interactive_required: /advisor needs a picker; pass '{AdvisorUsage}' or run without --json.").ConfigureAwait(false);
            return;
        }

        try
        {
            var settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
            if (choice.Length == 0)
            {
                await PickAdvisorAsync(settings, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(choice, AdvisorOffChoice, StringComparison.OrdinalIgnoreCase))
            {
                await SaveAdvisorAsync(null, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            var parts = choice.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var backend = parts.Length == 1 ? ReadBackend(settings) : NormalizeBackendArgument(parts[0]);
            var model = parts[^1];
            if (string.IsNullOrWhiteSpace(backend) || model.Any(char.IsWhiteSpace))
            {
                await _err.WriteLineAsync($"usage: {AdvisorUsage}").ConfigureAwait(false);
                await _err.FlushAsync().ConfigureAwait(false);
                return;
            }

            await SaveAdvisorAsync(backend, model, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentApiException error)
        {
            await _err.WriteLineAsync(CliErrorText.Format(error)).ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
        }
    }

    private async Task PickAdvisorAsync(JsonElement settings, CancellationToken cancellationToken)
    {
        var (currentBackend, currentModel) = ReadAdvisor(settings);
        if (Picker is null)
        {
            WriteAdvisorPanel(currentBackend, currentModel);
            return;
        }

        var keyStates = await ReadAllKeyStatesAsync(settings, cancellationToken).ConfigureAwait(false);
        var (providers, providerError) = await ReadModelsDevProvidersAsync(cancellationToken).ConfigureAwait(false);
        var offDetail = currentModel is null ? "no advisor · current" : "no advisor";
        var providerChoices = new List<Tui.ChoiceItem> { new(AdvisorOffChoice, offDetail, AdvisorOffChoice) };
        providerChoices.AddRange(Tui.TuiSurfaces.BackendChoices(currentBackend, keyStates, providers));
        var pickedBackend = await Picker(new Tui.ChoiceList("advisor provider", providerChoices,
            footer: $"{ProviderListNotice(providers, providerError)} · applies to new runs"), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (pickedBackend is null) return;
        if (pickedBackend == AdvisorOffChoice)
        {
            await SaveAdvisorAsync(null, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<(string Id, bool Supported)> catalog;
        try
        {
            catalog = await _settingsClient.GetModelsAsync(pickedBackend, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            await _err.WriteLineAsync($"advisor_catalog_unavailable: {CliErrorText.ShortMessage(error.Message)}. Use '/advisor {pickedBackend} <model>'.").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }

        var sameBackend = string.Equals(pickedBackend, currentBackend, StringComparison.OrdinalIgnoreCase);
        var modelChoices = Tui.TuiSurfaces.ModelCatalogChoices(pickedBackend, sameBackend ? currentModel : null, catalog, keyStates);
        if (modelChoices.Count == 0)
        {
            await _err.WriteLineAsync($"advisor_catalog_unavailable: {pickedBackend} lists no models. Use '/advisor {pickedBackend} <model>'.").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }

        var pickedModel = await Picker(new Tui.ChoiceList("advisor model", modelChoices, footer: pickedBackend, showRowCount: true),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(pickedModel)) return;
        await SaveAdvisorAsync(pickedBackend, pickedModel, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAdvisorAsync(string? backend, string? model, CancellationToken cancellationToken)
    {
        // Both fields travel together: two strings select, two nulls clear (the Host rejects a half).
        var body = new JsonObject { ["advisor_backend"] = backend, ["advisor_model"] = model };
        await _settingsClient.UpdateAsync(body, cancellationToken).ConfigureAwait(false);
        var summary = model is null ? "off" : $"{model} ({backend})";
        await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ advisor: {summary} for new runs")).ConfigureAwait(false);
        await _humanOut.FlushAsync().ConfigureAwait(false);
    }

    private void WriteAdvisorPanel(string? backend, string? model)
    {
        var rows = new List<Tui.PanelRow>
        {
            new("advisor", model is null ? "off" : model),
            new("provider", backend ?? "-"),
            new("applies to", "new runs; the main model decides when to consult"),
        };
        foreach (var line in Tui.TuiPanel.Compose(PanelWidth, "advisor", [new Tui.PanelSection(null, rows)], AdvisorUsage, framed: !FramelessReports))
        {
            _humanOut.WriteLine(line);
        }

        _humanOut.Flush();
    }

    private static (string? Backend, string? Model) ReadAdvisor(JsonElement settings)
    {
        static string? Text(JsonElement settings, string name) =>
            settings.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

        var backend = Text(settings, "advisor_backend");
        var model = Text(settings, "advisor_model");
        return backend is null || model is null ? (null, null) : (backend, model);
    }
}
