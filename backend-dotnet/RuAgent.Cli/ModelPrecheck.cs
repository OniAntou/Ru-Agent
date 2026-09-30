using System.Text.Json;
using RuAgent.Client;

namespace RuAgent.Cli;

/// <summary>
/// A best-effort check that the selected model id is one the live catalog lists.
/// </summary>
/// <remarks>
/// An invalid models.dev model id only surfaces mid-run as <c>model_unavailable</c>, after
/// a turn has already been spent. The check warns instead of gating: the catalog can be
/// stale, so a miss is not proof the id is wrong and the run continues either way. It runs
/// once when a chat or run starts and only for models.dev backends, whose ids are the
/// catalog's; the OpenAI, Anthropic and custom id spaces are not catalog-bound.
/// </remarks>
public static class ModelPrecheck
{
    /// <summary>The backend prefix whose model ids come from the live catalog.</summary>
    public const string ModelsDevPrefix = "models.dev:";

    private const int MaxModelIdChars = 120;

    /// <summary>
    /// Returns one bounded warning when the model looks wrong, or null when there is
    /// nothing to say.
    /// </summary>
    public static async Task<string?> CheckAsync(
        SettingsApiClient settingsClient,
        string? backend,
        string? model,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settingsClient);

        if (backend is null
            || !backend.Trim().StartsWith(ModelsDevPrefix, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var selected = model.Trim();
        IReadOnlyList<(string Id, bool Supported)> catalog;
        try
        {
            catalog = await settingsClient.GetModelsAsync(backend, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The catalog is an optimization, not a source of truth: failing to read it
            // never blocks the run, but the operator is told the check did not happen.
            return $"model_check_unavailable: {CliErrorText.ShortMessage(ex.Message)}";
        }

        if (catalog.Any(entry => string.Equals(entry.Id, selected, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return $"model_not_in_catalog: {SingleLine(selected)} - it may fail with a 404; choose another with /model";
    }

    /// <summary>
    /// Flattens and caps the id so the warning stays one bounded line, the same
    /// guarantee <see cref="CliErrorText"/> gives server text.
    /// </summary>
    private static string SingleLine(string text)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= MaxModelIdChars ? flat : flat[..(MaxModelIdChars - 1)] + "…";
    }

    /// <summary>
    /// Reads the active backend and model and runs <see cref="CheckAsync"/>.
    /// </summary>
    /// <remarks>
    /// The settings read is best-effort: an unreachable settings store has nothing to say
    /// about the model, and the run's own failure path will name the real problem.
    /// </remarks>
    public static async Task<string?> CheckCurrentAsync(
        SettingsApiClient settingsClient,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settingsClient);

        JsonElement settings;
        try
        {
            settings = await settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        var backend = settings.TryGetProperty("backend", out var backendValue) && backendValue.ValueKind == JsonValueKind.String
            ? backendValue.GetString()
            : null;
        var model = settings.TryGetProperty("model", out var modelValue) && modelValue.ValueKind == JsonValueKind.String
            ? modelValue.GetString()
            : null;
        return await CheckAsync(settingsClient, backend, model, cancellationToken).ConfigureAwait(false);
    }
}
