using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;

namespace RuAgent.Cli;

public sealed partial class AgentSlashCommands
{
    private const string AdvisorUsage = "/advisor [off | <model> | <provider> <model>]";
    private const string AdvisorOffChoice = "off";

    /// <summary>
    /// The advisor chosen in a chat whose session does not exist yet (the terminal creates it
    /// with the first message). It is saved to that session as soon as it is created, so
    /// choosing an advisor never leaves an empty session behind.
    /// </summary>
    private (bool IsSet, SessionAdvisor? Advisor) _pendingAdvisor;

    /// <summary>
    /// Saves an advisor chosen before the session existed to the session just created. A
    /// failure is reported: the run then starts without the advisor the user chose.
    /// </summary>
    public async Task ApplyPendingAdvisorAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_pendingAdvisor.IsSet) return;
        var pending = _pendingAdvisor.Advisor;
        _pendingAdvisor = default;
        try
        {
            await _settingsClient.SetSessionAdvisorAsync(sessionId, pending, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AgentApiException or HttpRequestException)
        {
            await _err.WriteLineAsync($"advisor_not_saved: {CliErrorText.ShortMessage(error.Message)}; this session has no advisor. Choose again with /advisor.").ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>/advisor</c>: shows or saves the model the main model may consult in this session
    /// (ADR 0047). A new session has none. The bare form picks a provider and then a model;
    /// <c>&lt;model&gt;</c> uses the active provider, <c>&lt;provider&gt; &lt;model&gt;</c>
    /// names one. It applies to the session's next run.
    /// </summary>
    private async Task HandleAdvisorAsync(string? arg, string? sessionId, CancellationToken cancellationToken)
    {
        // A pending choice belongs only to a chat that has not created its session yet.
        if (sessionId is not null) _pendingAdvisor = default;
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
            var current = sessionId is null
                ? _pendingAdvisor.Advisor
                : await _settingsClient.GetSessionAdvisorAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (choice.Length == 0)
            {
                await PickAdvisorAsync(settings, current, sessionId, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(choice, AdvisorOffChoice, StringComparison.OrdinalIgnoreCase))
            {
                await SaveAdvisorAsync(sessionId, null, null, cancellationToken).ConfigureAwait(false);
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

            await SaveAdvisorAsync(sessionId, backend, model, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AgentApiException error)
        {
            await _err.WriteLineAsync(CliErrorText.Format(error)).ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
        }
    }

    private async Task PickAdvisorAsync(
        JsonElement settings, SessionAdvisor? current, string? sessionId, CancellationToken cancellationToken)
    {
        var (currentBackend, currentModel) = (current?.Backend, current?.Model);
        if (Picker is null)
        {
            WriteAdvisorPanel(currentBackend, currentModel);
            return;
        }

        var keyStates = await ReadAllKeyStatesAsync(settings, cancellationToken).ConfigureAwait(false);
        var (providers, providerError) = await ReadModelsDevProvidersAsync(cancellationToken).ConfigureAwait(false);
        var offDetail = currentModel is null ? "no advisor · current" : "no advisor";
        var providerChoices = new List<Tui.ChoiceItem> { new(AdvisorOffChoice, offDetail, AdvisorOffChoice) };
        providerChoices.AddRange(Tui.TuiSurfaces.BackendChoices(currentBackend, keyStates, providers, ReadKeyStatuses(settings)));
        var pickedBackend = await Picker(new Tui.ChoiceList("advisor provider", providerChoices,
            footer: $"{ProviderListNotice(providers, providerError)} · this session's next run"), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (pickedBackend is null) return;
        if (pickedBackend == AdvisorOffChoice)
        {
            await SaveAdvisorAsync(sessionId, null, null, cancellationToken).ConfigureAwait(false);
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
        await SaveAdvisorAsync(sessionId, pickedBackend, pickedModel, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAdvisorAsync(string? sessionId, string? backend, string? model, CancellationToken cancellationToken)
    {
        var advisor = backend is null || model is null ? null : new SessionAdvisor(backend, model);
        var summary = advisor is null ? "off" : $"{advisor.Model} ({advisor.Backend})";
        if (sessionId is null)
        {
            _pendingAdvisor = (true, advisor);
            await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ advisor: {summary} for this session, from its first message")).ConfigureAwait(false);
        }
        else
        {
            await _settingsClient.SetSessionAdvisorAsync(sessionId, advisor, cancellationToken).ConfigureAwait(false);
            await _humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ advisor: {summary} for this session's next run")).ConfigureAwait(false);
        }

        await _humanOut.FlushAsync().ConfigureAwait(false);
    }

    private void WriteAdvisorPanel(string? backend, string? model)
    {
        var rows = new List<Tui.PanelRow>
        {
            new("advisor", model is null ? "off" : model),
            new("provider", backend ?? "-"),
            new("applies to", "this session's next run; the main model decides when to consult"),
        };
        foreach (var line in Tui.TuiPanel.Compose(PanelWidth, "advisor", [new Tui.PanelSection(null, rows)], AdvisorUsage, framed: !FramelessReports))
        {
            _humanOut.WriteLine(line);
        }

        _humanOut.Flush();
    }
}
