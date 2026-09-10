using System.Text.Json.Nodes;
using RuBackend.Providers;

namespace RuBackend.Business.Settings;

public interface IRuntimeSettingsLoader
{
    Task<string> LoadAsync(
        SettingsSnapshot candidate,
        CancellationToken cancellationToken);
}

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
    private const string OpenCodeAccount = "api_key";
    private const string KiloAccount = "kilo_gateway_api_key";
    private const string GeminiAccount = "gemini_vision_api_key";
    private const string CustomAccount = "custom_api_key";
    private const string OpenAiAccount = "openai_api_key";
    private const string AnthropicAccount = "anthropic_api_key";
    private const string BackendOpenCode = "OpenCode Zen";
    private const string BackendKilo = "Kilo Gateway";
    private const string BackendOpenAi = "OpenAI";
    private const string BackendAnthropic = "Anthropic";

    private readonly ISettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly IRuntimeSettingsLoader _runtime;
    private readonly ProviderCatalogService? _catalog;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsTransitionService(
        ISettingsStore settings,
        ICredentialStore credentials,
        IRuntimeSettingsLoader runtime,
        ProviderCatalogService? catalog = null)
    {
        _settings = settings;
        _credentials = credentials;
        _runtime = runtime;
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
                candidate = BuildCandidate(ProviderReasoningPreferences.CaptureLegacy(previous), payload);
                updates = BuildCredentialUpdates(candidate.Backend, payload);
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
                    candidate = ProviderReasoningPreferences.Save(candidate, effort);
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

                var requiresRuntimeLoad = RequiresRuntimeLoad(payload, updates);
                var runtimeMessage = requiresRuntimeLoad
                    ? await _runtime.LoadAsync(candidate, cancellationToken).ConfigureAwait(false)
                    : "Settings saved.";
                try
                {
                    await _settings.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await RestoreCredentialsAsync(snapshots).ConfigureAwait(false);
                    if (requiresRuntimeLoad)
                    {
                        await TryRestoreRuntimeAsync(previous).ConfigureAwait(false);
                    }

                    throw;
                }

                return new SettingsTransitionResult(
                    true,
                    string.IsNullOrWhiteSpace(runtimeMessage)
                        ? "Settings saved."
                        : runtimeMessage);
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
            var current = await _settings.ReadAsync(cancellationToken).ConfigureAwait(false);
            ProviderSelection selection;
            try
            {
                selection = ProviderSelection.From(current, backendChoice);
            }
            catch (Exception error) when (error is UnknownProviderAliasException or ArgumentException)
            {
                return new SettingsTransitionResult(false, "Unknown provider.");
            }

            if (selection.Account is null)
            {
                return new SettingsTransitionResult(
                    true,
                    "This provider does not use a stored credential.");
            }

            var existing = await _credentials.GetAsync(
                ServiceName,
                selection.Account,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(existing))
            {
                return new SettingsTransitionResult(true, "Provider is already signed out.");
            }

            snapshots.Add(new CredentialSnapshot(selection.Account, existing));
            await _credentials.DeleteAsync(
                ServiceName,
                selection.Account,
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

    private async Task TryRestoreRuntimeAsync(SettingsSnapshot previous)
    {
        try
        {
            await _runtime.LoadAsync(previous, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The caller receives one redacted transition failure.
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
        BackendOpenCode => "opencode_model",
        BackendKilo => "kilo_model",
        "Ollama" => "ollama_model",
        "LM Studio" => "lmstudio_model",
        "Custom OpenAI" => "custom_model",
        BackendOpenAi => "openai_model",
        BackendAnthropic => "anthropic_model",
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
        var openCodeModel = OptionalString(payload, "opencode_model", 256)
            ?? current.OpenCodeModel;
        var kiloModel = OptionalString(payload, "kilo_model", 256)
            ?? current.KiloModel;
        var ollamaModel = OptionalString(payload, "ollama_model", 256)
            ?? current.OllamaModel;
        var lmStudioModel = OptionalString(payload, "lmstudio_model", 256)
            ?? current.LmStudioModel;
        var customModel = OptionalString(payload, "custom_model", 256)
            ?? current.CustomModel;
        var ollamaUrl = OptionalString(payload, "ollama_url", 1024)
            ?? current.OllamaUrl;
        var lmStudioUrl = OptionalString(payload, "lmstudio_url", 1024)
            ?? current.LmStudioUrl;
        var customUrl = OptionalString(payload, "custom_url", 1024)
            ?? current.CustomUrl;
        var openAiModel = OptionalString(payload, "openai_model", 256)
            ?? current.OpenAiModel;
        var anthropicModel = OptionalString(payload, "anthropic_model", 256)
            ?? current.AnthropicModel;
        var apiVariant = OptionalString(payload, "api_variant", 64) ?? current.ApiVariant;
        var voiceStyle = OptionalString(payload, "voice_style_id", 32)
            ?? current.VoiceStyleId;
        if (!string.Equals(voiceStyle, "original", StringComparison.Ordinal))
        {
            throw new SettingsPayloadException();
        }

        return new SettingsSnapshot(
            current.Raw.DeepClone().AsObject(),
            backend,
            model,
            openCodeModel,
            kiloModel,
            apiVariant,
            OptionalBoolean(payload, "enable_self_learning")
                ?? OptionalBoolean(payload, "self_learning_enabled")
                ?? current.EnableSelfLearning,
            OptionalBoolean(payload, "share_memory_with_cloud")
                ?? OptionalBoolean(payload, "cloud_memory_consent")
                ?? current.ShareMemoryWithCloud,
            OptionalBoolean(payload, "kilo_reasoning_enabled")
                ?? current.KiloReasoningEnabled,
            OptionalString(payload, "voice_label", 128) ?? current.VoiceLabel,
            voiceStyle,
            OptionalDouble(payload, "voice_speed", 0.5, 2.0) ?? current.VoiceSpeed,
            OptionalBoolean(payload, "auto_pace_enabled") ?? current.AutoPaceEnabled,
            OptionalBoolean(payload, "auto_read_replies") ?? current.AutoReadReplies,
            OptionalBoolean(payload, "show_subtitles") ?? current.ShowSubtitles,
            OptionalString(payload, "subtitle_color", 32) ?? current.SubtitleColor,
            OptionalString(payload, "subtitle_size", 32) ?? current.SubtitleSize,
            OptionalDouble(payload, "subtitle_duration", 1.0, 10.0) ?? current.SubtitleDuration,
            OptionalString(payload, "subtitle_pos_mode", 32) ?? current.SubtitlePosMode,
            OptionalInt(payload, "subtitle_pos_x", -1, 10000) ?? current.SubtitlePosX,
            OptionalInt(payload, "subtitle_pos_y", -1, 10000) ?? current.SubtitlePosY,
            ollamaModel,
            lmStudioModel,
            customModel,
            ollamaUrl,
            lmStudioUrl,
            customUrl,
            openAiModel,
            anthropicModel);
    }

    private static IReadOnlyList<CredentialUpdate> BuildCredentialUpdates(
        string backend,
        JsonObject payload)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var openCode = OptionalSecret(payload, "opencode_api_key");
        var kilo = OptionalSecret(payload, "kilo_api_key");
        var gemini = OptionalSecret(payload, "gemini_vision_api_key");
        var custom = OptionalSecret(payload, "custom_api_key");
        var openAi = OptionalSecret(payload, "openai_api_key");
        var anthropic = OptionalSecret(payload, "anthropic_api_key");
        var legacy = OptionalSecret(payload, "api_key");
        if (openCode is not null)
        {
            updates[OpenCodeAccount] = openCode;
        }

        if (kilo is not null)
        {
            updates[KiloAccount] = kilo;
        }

        if (gemini is not null)
        {
            updates[GeminiAccount] = gemini;
        }

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

        if (legacy is not null)
        {
            if (backend == BackendOpenCode && openCode is null)
            {
                updates[OpenCodeAccount] = legacy;
            }
            else if (backend == BackendKilo && kilo is null)
            {
                updates[KiloAccount] = legacy;
            }
        }

        return updates.Select(pair => new CredentialUpdate(pair.Key, pair.Value)).ToArray();
    }

    private static bool RequiresRuntimeLoad(
        JsonObject payload,
        IReadOnlyList<CredentialUpdate> updates)
    {
        var runtimeFields = new[]
        {
            "backend",
            "backend_choice",
            "model",
            "opencode_model",
            "kilo_model",
            "ollama_model",
            "lmstudio_model",
            "custom_model",
            "ollama_url",
            "lmstudio_url",
            "custom_url",
            "openai_model",
            "anthropic_model",
            "openai_api_key",
            "anthropic_api_key",
            "custom_api_key",
            "api_variant",
            "kilo_reasoning_enabled",
            "voice_label",
            "voice_style_id",
            "voice_speed",
            "auto_pace_enabled",
            "auto_read_replies",
            "show_subtitles",
            "subtitle_color",
            "subtitle_size",
            "subtitle_duration",
            "subtitle_pos_mode",
            "subtitle_pos_x",
            "subtitle_pos_y",
        };
        return runtimeFields.Any(payload.ContainsKey)
            || updates.Any(update =>
                update.Account is OpenCodeAccount or KiloAccount or CustomAccount or OpenAiAccount or AnthropicAccount);
    }

    private static string NormalizeBackend(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "gguf" or "llama.cpp" => BackendOpenCode,
            "opencode" or "opencode zen" or "opencode zen api" or "opencode api" => BackendOpenCode,
            "kilo" or "kilo gateway" or "kilo gateway api" or "kilo api" => BackendKilo,
            "ollama" or "ollama api" => "Ollama",
            "lmstudio" or "lm studio" or "lm studio api" => "LM Studio",
            "custom" or "custom openai" or "custom openai api" => "Custom OpenAI",
            "openai" or "openai api" => BackendOpenAi,
            "anthropic" or "anthropic claude" or "claude" => BackendAnthropic,
            _ => throw new SettingsPayloadException(),
        };

    private static string? OptionalSecret(JsonObject payload, string key)
    {
        var value = OptionalString(payload, key, 4096);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

    private static bool? OptionalBoolean(JsonObject payload, string key)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value && value.TryGetValue<bool>(out var result))
        {
            return result;
        }

        throw new SettingsPayloadException();
    }

    private static int? OptionalInt(
        JsonObject payload,
        string key,
        int minimum,
        int maximum)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value
            && value.TryGetValue<int>(out var result)
            && result >= minimum
            && result <= maximum)
        {
            return result;
        }

        throw new SettingsPayloadException();
    }

    private static double? OptionalDouble(
        JsonObject payload,
        string key,
        double minimum,
        double maximum)
    {
        if (!payload.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue value
            && TryReadDouble(value, out var result)
            && double.IsFinite(result)
            && result >= minimum
            && result <= maximum)
        {
            return result;
        }

        throw new SettingsPayloadException();
    }

    private static bool TryReadDouble(JsonValue value, out double result)
    {
        if (value.TryGetValue<double>(out result))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var integer))
        {
            result = integer;
            return true;
        }

        result = 0;
        return false;
    }

    private sealed record CredentialUpdate(string Account, string Secret);

    private sealed record CredentialSnapshot(string Account, string? Secret);

    private sealed class SettingsPayloadException : Exception
    {
    }
}
