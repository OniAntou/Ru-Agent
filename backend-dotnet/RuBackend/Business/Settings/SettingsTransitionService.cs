using System.Text.Json.Nodes;
using RuAgent.Protocol.Providers;
using RuBackend.Providers;

namespace RuBackend.Business.Settings;

public sealed record SettingsTransitionResult(bool Success, string Message, string? Code = null);

public interface ISettingsTransitionService
{
    Task<SettingsTransitionResult> ApplyAsync(
        JsonObject payload,
        CancellationToken cancellationToken);

    Task<SettingsTransitionResult> RemoveProviderAsync(
        string backendChoice,
        CancellationToken cancellationToken);

    Task RecordKeyStatusAsync(
        string backend,
        string account,
        string usedCredential,
        string state,
        Func<SettingsSnapshot, bool> endpointUnchanged,
        CancellationToken cancellationToken);
}

public sealed partial class SettingsTransitionService : ISettingsTransitionService
{
    private const string ServiceName = "Ru";
    private const string CustomAccount = "custom_api_key";
    private const string OpenAiAccount = "openai_api_key";
    private const string AnthropicAccount = "anthropic_api_key";
    private const string BackendOpenAi = "OpenAI";
    private const string BackendAnthropic = "Anthropic";

    /// <summary>Set only by the key route; asks for the candidate to be checked before it is stored.</summary>
    internal const string ValidateKeyField = "validate_provider_key";

    private readonly ISettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly ProviderCatalogService? _catalog;
    private readonly RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor? _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsTransitionService(
        ISettingsStore settings,
        ICredentialStore credentials,
        ProviderCatalogService? catalog = null,
        RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor? secrets = null)
    {
        _settings = settings;
        _credentials = credentials;
        _catalog = catalog;
        _secrets = secrets;
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
            var savedMessage = "Settings saved.";
            string? savedCode = null;
            CandidateKeyVerdict? verdict = null;
            try
            {
                payload = ExpandModelSelection(payload);
                candidate = BuildCandidate(previous, payload);
                updates = BuildCredentialUpdates(payload);
                if (await SendsKeyInsecurelyAsync(previous, candidate, updates, cancellationToken).ConfigureAwait(false))
                    return new(false, "Key not sent: this endpoint uses plain HTTP to another computer. Use HTTPS, or remove the key.",
                        CustomProviders.InsecureEndpointCode);
                if (payload.ContainsKey(ValidateKeyField))
                {
                    if (payload[ValidateKeyField] is not JsonValue flag || !flag.TryGetValue<bool>(out var validate) || !validate
                        || updates.Count != 1 || _catalog is null)
                        throw new SettingsPayloadException();
                    var alias = ValidatedKeyAlias(payload, updates[0].Account) ?? throw new SettingsPayloadException();
                    verdict = await _catalog.VerifyCandidateCredentialAsync(alias, updates[0].Secret, previous, cancellationToken)
                        .ConfigureAwait(false);
                    switch (verdict)
                    {
                        case CandidateKeyVerdict.Rejected:
                            return new(false, "Key not saved; previous credential unchanged.", "provider_key_rejected");
                        case CandidateKeyVerdict.Unavailable:
                            return new(false, "Key not saved; previous credential unchanged.", "provider_key_unverified");
                        case CandidateKeyVerdict.Unchecked:
                            savedMessage = "Key saved, but it could not be checked before use; the first request will.";
                            savedCode = "provider_key_unchecked";
                            break;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                }
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
            catch (ProviderKeyFormatException error)
            {
                return new SettingsTransitionResult(false, error.Message, error.Code);
            }
            catch (Exception error) when (error is SettingsPayloadException or ProviderCatalogException or ArgumentException)
            {
                return new SettingsTransitionResult(false, "Invalid settings payload.", "settings_invalid");
            }

            // What is known about each key follows this save, in the same write: a moved
            // endpoint forgets its verdict, and a new key carries the check it just had.
            ProviderKeyStatus.ForgetMovedEndpoints(previous, candidate, candidate.Raw);
            foreach (var update in updates)
            {
                if (ProviderKeyStatus.BackendForAccount(update.Account) is { } keyed)
                    ProviderKeyStatus.Set(candidate.Raw, keyed,
                        verdict == CandidateKeyVerdict.Accepted ? ProviderKeyStatus.Verified : ProviderKeyStatus.Unchecked,
                        DateTimeOffset.UtcNow);
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

                // A stored key is redacted from runs from now on, not only after restart.
                _secrets?.AddStoredSecrets(updates.Select(update => update.Secret));
                return new SettingsTransitionResult(true, savedMessage, savedCode);
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
                    "Runtime settings could not be applied.",
                    "settings_apply_failed");
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
            await ForgetKeyStatusAsync(account).ConfigureAwait(false);
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

    /// <summary>
    /// Records what a run learned about a stored key. Nothing is written unless the
    /// key is still the one the run used, its endpoint is unchanged, and the state is
    /// new: a verdict about an old key must never land on the key that replaced it.
    /// </summary>
    public async Task RecordKeyStatusAsync(
        string backend,
        string account,
        string usedCredential,
        string state,
        Func<SettingsSnapshot, bool> endpointUnchanged,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentNullException.ThrowIfNull(usedCredential);
        ArgumentNullException.ThrowIfNull(endpointUnchanged);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (ProviderKeyStatus.Read(current, backend) == state || !endpointUnchanged(current)) return;
            var stored = await _credentials.GetAsync(ServiceName, account, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(stored, usedCredential, StringComparison.Ordinal)) return;
            var raw = current.Raw.DeepClone().AsObject();
            ProviderKeyStatus.Set(raw, backend, state, DateTimeOffset.UtcNow);
            await _settings.SaveAsync(current with { Raw = raw }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Best effort: a signed-out provider keeps no verdict about its old key.</summary>
    private async Task ForgetKeyStatusAsync(string account)
    {
        if (ProviderKeyStatus.BackendForAccount(account) is not { } backend) return;
        try
        {
            var current = await _settings.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (ProviderKeyStatus.Read(current, backend) is null) return;
            var raw = current.Raw.DeepClone().AsObject();
            ProviderKeyStatus.Remove(raw, backend);
            await _settings.SaveAsync(current with { Raw = raw }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The key is gone; a leftover verdict is shown only while a key is stored.
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
        _ when CustomProviders.IsCustomBackend(backend) => "model",
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
        var customUrlValue = OptionalString(payload, "custom_url", 1024);
        var customUrl = customUrlValue is null ? current.CustomUrl : CustomProviders.NormalizeLegacyUrl(customUrlValue);
        var openAiModel = OptionalString(payload, "openai_model", 256)
            ?? current.OpenAiModel;
        var anthropicModel = OptionalString(payload, "anthropic_model", 256)
            ?? current.AnthropicModel;
        var cloudflareAccountId = OptionalString(payload, "cloudflare_account_id", 64)
            ?? current.CloudflareAccountId;

        var raw = current.Raw.DeepClone().AsObject();
        Providers.ComputerUsePolicy.Apply(payload, raw);
        Providers.ProviderWebSearchPolicy.Apply(payload, raw);
        CustomProviders.Apply(payload, raw);
        Providers.AdvisorPolicy.Apply(payload, raw, NormalizeBackend);
        // Selecting a named custom provider requires its definition in the same settings.
        if (backendValue is not null && CustomProviders.IsCustomBackend(backend)
            && CustomProviders.Find(current with { Raw = raw }, backend) is null)
            throw new SettingsPayloadException();
        if (payload.ContainsKey(Providers.AdvisorPolicy.BackendField)
            && Providers.AdvisorPolicy.Read(current with { Raw = raw }) is { } advisor
            && CustomProviders.IsCustomBackend(advisor.Backend)
            && CustomProviders.Find(current with { Raw = raw }, advisor.Backend) is null)
            throw new SettingsPayloadException();
        if (payload.ContainsKey("subagents")) throw new SettingsPayloadException();
        raw.Remove("subagents");
        return new SettingsSnapshot(
            raw,
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

        var customProviderId = OptionalString(payload, "custom_provider_id", 64);
        var customProviderKey = OptionalSecret(payload, "custom_provider_api_key");
        if (customProviderId is not null || customProviderKey is not null)
        {
            if (!CustomProviders.IsValidId(customProviderId) || customProviderKey is null)
                throw new SettingsPayloadException();
            updates[CustomProviders.CredentialAccount(customProviderId!)] = customProviderKey;
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
        if (ProviderAliases.Normalize(value) is { } named)
        {
            return named;
        }

        if (CustomProviders.NormalizeBackend(value) is { } custom)
        {
            return custom;
        }

        throw new SettingsPayloadException();
    }

    /// <summary>
    /// The provider a validated key is for, or null when the payload is not exactly one
    /// provider's key. A named custom provider must already be saved: its endpoint is
    /// read from the stored definition, never from the same request.
    /// </summary>
    private static string? ValidatedKeyAlias(JsonObject payload, string account) => account switch
    {
        OpenAiAccount when payload.Count == 2 => "openai",
        AnthropicAccount when payload.Count == 2 => "anthropic",
        CustomAccount when payload.Count == 2 => "custom",
        _ when payload.Count == 3 && payload.ContainsKey("models_dev_provider_id") =>
            ProviderSelection.NormalizeModelsDevBackend(OptionalString(payload, "models_dev_provider_id", 64) ?? ""),
        _ when payload.Count == 3 && payload.ContainsKey("custom_provider_id")
            && !payload.ContainsKey(CustomProviders.SettingField)
            && OptionalString(payload, "custom_provider_id", 64) is { } id
            && CustomProviders.IsValidId(id) && account == CustomProviders.CredentialAccount(id) =>
            CustomProviders.BackendPrefix + id,
        _ => null,
    };

    private static string? OptionalSecret(JsonObject payload, string key)
    {
        var value = OptionalString(payload, key, 4096);
        // Secrets are stored byte-exact: surrounding whitespace can be part of a
        // pasted key, so only an empty value is rejected here.
        if (string.IsNullOrWhiteSpace(value)) return null;
        // A line break or other control character cannot travel in an HTTP header, so
        // such a key failed every request later with an unnamed error. Say so now.
        if (value.Any(char.IsControl))
            throw new ProviderKeyFormatException("provider_key_malformed",
                "Key not saved: it contains a line break or another control character.");
        if (value.Length > WindowsCredentialStore.MaximumSecretCharacters)
            throw new ProviderKeyFormatException("provider_key_too_long",
                $"Key not saved: Windows Credential Manager holds at most {WindowsCredentialStore.MaximumSecretCharacters} characters.");
        return value;
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

    /// <summary>A key the vault or an HTTP header cannot carry; never holds the key.</summary>
    private sealed class ProviderKeyFormatException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
