using System.Text.Json.Nodes;
using RuBackend.Providers;

namespace RuBackend.Business.Settings;

public sealed record SettingsTransitionResult(bool Success, string Message);

public interface ISettingsTransitionService
{
    Task<SettingsTransitionResult> ApplyAsync(
        JsonObject payload,
        CancellationToken cancellationToken);

    Task<SettingsTransitionResult> RemoveProviderAsync(
        string backendChoice,
        CancellationToken cancellationToken);
}

public sealed class SettingsTransitionService : ISettingsTransitionService
{
    private const string ServiceName = "Ru";
    private const string CustomAccount = "custom_api_key";
    private const string OpenAiAccount = "openai_api_key";
    private const string AnthropicAccount = "anthropic_api_key";
    private const string BackendOpenAi = "OpenAI";
    private const string BackendAnthropic = "Anthropic";

    private readonly ISettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly ProviderCatalogService? _catalog;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsTransitionService(
        ISettingsStore settings,
        ICredentialStore credentials,
        ProviderCatalogService? catalog = null)
    {
        _settings = settings;
        _credentials = credentials;
        _catalog = catalog;
    }

    public async Task<SettingsTransitionResult> ApplyAsync(
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);
            SettingsSnapshot candidate;
            IReadOnlyList<CredentialUpdate> updates;
            try
            {
                payload = ExpandModelSelection(payload);
                candidate = BuildCandidate(previous, payload);
                updates = BuildCredentialUpdates(payload);
                if (payload.ContainsKey("reasoning_effort"))
                {
                    if (_catalog is null || updates.Count != 0) throw new SettingsPayloadException();
                    // Reasoning saves are intentionally partial and selection-bound.
                    if (payload.Count != 3) throw new SettingsPayloadException();
                    var effort = OptionalString(payload, "reasoning_effort", 32) ?? throw new SettingsPayloadException();
                    var model = OptionalString(payload, "reasoning_model", 256);
                    var identity = OptionalString(payload, "reasoning_identity", 64);
                    var snapshot = await _catalog.GetAsync(previous, false, cancellationToken).ConfigureAwait(false);
                    if (snapshot.Identity != identity || snapshot.SelectedModel != model) throw new SettingsPayloadException();
                    if (effort != "default") ProviderReasoningPreferences.Resolve(snapshot, model, effort);
                    // The catalog, not the bare settings, is what gives a models.dev
                    // backend a base URL to key the stored choice against.
                    var selection = await _catalog.ResolveSelectionAsync(candidate, cancellationToken)
                        .ConfigureAwait(false);
                    candidate = ProviderReasoningPreferences.Save(candidate, effort, selection);
                }
            }
            catch (Exception error) when (error is SettingsPayloadException or ProviderCatalogException or ArgumentException)
            {
                return new SettingsTransitionResult(false, "Invalid settings payload.");
            }

            var snapshots = new List<CredentialSnapshot>();
            try
            {
                foreach (var update in updates)
                {
                    var oldValue = await _credentials.GetAsync(
                        ServiceName,
                        update.Account,
                        cancellationToken).ConfigureAwait(false);
                    snapshots.Add(new CredentialSnapshot(update.Account, oldValue));
                    await _credentials.SetAsync(
                        ServiceName,
                        update.Account,
                        update.Secret,
                        cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await _settings.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await RestoreCredentialsAsync(snapshots).ConfigureAwait(false);
                    throw;
                }

                return new SettingsTransitionResult(true, "Settings saved.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RestoreCredentialsAsync(snapshots).ConfigureAwait(false);
                throw;
            }
            catch
            {
                await RestoreCredentialsAsync(snapshots).ConfigureAwait(false);
                return new SettingsTransitionResult(
                    false,
                    "Runtime settings could not be applied.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SettingsTransitionResult> RemoveProviderAsync(
        string backendChoice,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendChoice);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = new List<CredentialSnapshot>();
        try
        {
            string? account;
            try
            {
                account = ProviderSelection.CredentialAccountForAlias(backendChoice);
            }
            catch (Exception error) when (error is UnknownProviderAliasException or ArgumentException)
            {
                return new SettingsTransitionResult(false, "Unknown provider.");
            }

            if (account is null)
            {
                return new SettingsTransitionResult(
                    true,
                    "This provider does not use a stored credential.");
            }

            var existing = await _credentials.GetAsync(
                ServiceName,
                account,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(existing))
            {
                return new SettingsTransitionResult(true, "Provider is already signed out.");
            }

            snapshots.Add(new CredentialSnapshot(account, existing));
            await _credentials.DeleteAsync(
                ServiceName,
                account,
                cancellationToken).ConfigureAwait(false);
            return new SettingsTransitionResult(true, "Provider signed out.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RestoreCredentialsAsync(snapshots).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await RestoreCredentialsAsync(snapshots).ConfigureAwait(false);
            return new SettingsTransitionResult(false, "Provider could not be removed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RestoreCredentialsAsync(
        IEnumerable<CredentialSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots.Reverse())
        {
            try
            {
                if (snapshot.Secret is null)
                {
                    await _credentials.DeleteAsync(
                        ServiceName,
                        snapshot.Account,
                        CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _credentials.SetAsync(
                        ServiceName,
                        snapshot.Account,
                        snapshot.Secret,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Do not replace the original transition failure with vault details.
            }
        }
    }

    private static JsonObject ExpandModelSelection(JsonObject payload)
    {
        if (!payload.ContainsKey("selected_model"))
        {
            return payload;
        }

        if (payload.Count != 2 || !payload.ContainsKey("backend_choice"))
        {
            throw new SettingsPayloadException();
        }

        var backend = NormalizeBackend(
            OptionalString(payload, "backend_choice", 64)
            ?? throw new SettingsPayloadException());
        var model = OptionalString(payload, "selected_model", 256)?.Trim()
            ?? throw new SettingsPayloadException();
        if (model.Length == 0)
        {
            throw new SettingsPayloadException();
        }

        var expanded = new JsonObject { ["backend_choice"] = backend };
        expanded[ModelFieldForBackend(backend)] = model;
        return expanded;
    }

    private static string ModelFieldForBackend(string backend) => backend switch
    {
        "Custom OpenAI" => "custom_model",
        BackendOpenAi => "openai_model",
        BackendAnthropic => "anthropic_model",
        _ when ProviderSelection.IsModelsDevBackend(backend) => "model",
        _ => throw new SettingsPayloadException(),
    };

    private static SettingsSnapshot BuildCandidate(
        SettingsSnapshot current,
        JsonObject payload)
    {
        var backendValue = OptionalString(payload, "backend_choice", 64)
            ?? OptionalString(payload, "backend", 64);
        var backend = backendValue is null ? current.Backend : NormalizeBackend(backendValue);
        var model = OptionalString(payload, "model", 256) ?? current.Model;
        var customModel = OptionalString(payload, "custom_model", 256)
            ?? current.CustomModel;
        var customUrl = OptionalString(payload, "custom_url", 1024)
            ?? current.CustomUrl;
        var openAiModel = OptionalString(payload, "openai_model", 256)
            ?? current.OpenAiModel;
        var anthropicModel = OptionalString(payload, "anthropic_model", 256)
            ?? current.AnthropicModel;
        var cloudflareAccountId = OptionalString(payload, "cloudflare_account_id", 64)
            ?? current.CloudflareAccountId;

        return new SettingsSnapshot(
            current.Raw.DeepClone().AsObject(),
            backend,
            model,
            customModel,
            customUrl,
            openAiModel,
            anthropicModel,
            current.CompactionTriggerRatio,
            current.CompactionTargetRatio)
        {
            CloudflareAccountId = cloudflareAccountId,
        };
    }

    private static IReadOnlyList<CredentialUpdate> BuildCredentialUpdates(JsonObject payload)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var custom = OptionalSecret(payload, "custom_api_key");
        var openAi = OptionalSecret(payload, "openai_api_key");
        var anthropic = OptionalSecret(payload, "anthropic_api_key");
        if (custom is not null)
        {
            updates[CustomAccount] = custom;
        }

        if (openAi is not null)
        {
            updates[OpenAiAccount] = openAi;
        }

        if (anthropic is not null)
        {
            updates[AnthropicAccount] = anthropic;
        }

        var modelsDevId = OptionalString(payload, "models_dev_provider_id", 64);
        var modelsDevKey = OptionalSecret(payload, "models_dev_api_key");
        if (modelsDevId is not null || modelsDevKey is not null)
        {
            var normalized = ProviderSelection.NormalizeModelsDevBackend(modelsDevId ?? string.Empty)
                ?? throw new SettingsPayloadException();
            if (modelsDevKey is null)
            {
                throw new SettingsPayloadException();
            }

            updates[normalized["models.dev:".Length..] + "_api_key"] = modelsDevKey;
        }

        return updates.Select(pair => new CredentialUpdate(pair.Key, pair.Value)).ToArray();
    }

    private static string NormalizeBackend(string value)
    {
        if (ProviderSelection.NormalizeModelsDevBackend(value) is { } generic)
        {
            return generic;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "gguf" or "llama.cpp" => "models.dev:opencode",
            "opencode" or "opencode zen" or "opencode zen api" or "opencode api" => "models.dev:opencode",
            "kilo" or "kilo gateway" or "kilo gateway api" or "kilo api" => "models.dev:kilo",
            "custom" or "custom openai" or "custom openai api" => "Custom OpenAI",
            "openai" or "openai api" => BackendOpenAi,
            "anthropic" or "anthropic claude" or "claude" => BackendAnthropic,
            _ => throw new SettingsPayloadException(),
        };
    }

    private static string? OptionalSecret(JsonObject payload, string key)
    {
        var value = OptionalString(payload, key, 4096);
        // Secrets are stored byte-exact: surrounding whitespace can be part of a
        // pasted key, so only an empty value is rejected here.
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? OptionalString(JsonObject payload, string key, int maximum)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonValue value
            || !value.TryGetValue<string>(out var result)
            || result.Length > maximum)
        {
            throw new SettingsPayloadException();
        }

        return result;
    }

    private sealed record CredentialUpdate(string Account, string Secret);

    private sealed record CredentialSnapshot(string Account, string? Secret);

    private sealed class SettingsPayloadException : Exception
    {
    }
}
