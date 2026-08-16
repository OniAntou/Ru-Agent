using System.Text.Json.Nodes;

namespace RuBackend.Business.Settings;

public interface IRuntimeSettingsLoader
{
    Task<string> LoadAsync(
        SettingsSnapshot candidate,
        CancellationToken cancellationToken);
}

public sealed class UnavailableRuntimeSettingsLoader : IRuntimeSettingsLoader
{
    public Task<string> LoadAsync(
        SettingsSnapshot candidate,
        CancellationToken cancellationToken) =>
        Task.FromException<string>(
            new InvalidOperationException("Private runtime worker is unavailable."));
}

public sealed record SettingsTransitionResult(bool Success, string Message);

public interface ISettingsTransitionService
{
    Task<SettingsTransitionResult> ApplyAsync(
        JsonObject payload,
        CancellationToken cancellationToken);
}

public sealed class SettingsTransitionService : ISettingsTransitionService
{
    private const string ServiceName = "Ru";
    private const string OpenCodeAccount = "api_key";
    private const string KiloAccount = "kilo_gateway_api_key";
    private const string GeminiAccount = "gemini_vision_api_key";
    private const string BackendGguf = "llama.cpp";
    private const string BackendOpenCode = "OpenCode Zen";
    private const string BackendKilo = "Kilo Gateway";

    private readonly ISettingsStore _settings;
    private readonly ICredentialStore _credentials;
    private readonly IRuntimeSettingsLoader _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsTransitionService(
        ISettingsStore settings,
        ICredentialStore credentials,
        IRuntimeSettingsLoader runtime)
    {
        _settings = settings;
        _credentials = credentials;
        _runtime = runtime;
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
                candidate = BuildCandidate(previous, payload);
                updates = BuildCredentialUpdates(candidate.Backend, payload);
            }
            catch (SettingsPayloadException)
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
        var apiVariant = OptionalString(payload, "api_variant", 64) ?? current.ApiVariant;
        var checkpoint = OptionalString(payload, "checkpoint_path", 1024);
        if (string.IsNullOrWhiteSpace(checkpoint))
        {
            checkpoint = current.CheckpointPath;
        }

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
            OptionalInt(payload, "max_tokens", 1, 1024)
                ?? OptionalInt(payload, "max_new_tokens", 1, 1024)
                ?? current.MaxTokens,
            OptionalDouble(payload, "temperature", 0.1, 2.0) ?? current.Temperature,
            OptionalInt(payload, "top_k", 1, 200) ?? current.TopK,
            OptionalBoolean(payload, "allow_web_search")
                ?? OptionalBoolean(payload, "allow_online_search")
                ?? current.AllowWebSearch,
            OptionalBoolean(payload, "enable_self_learning")
                ?? OptionalBoolean(payload, "self_learning_enabled")
                ?? current.EnableSelfLearning,
            OptionalBoolean(payload, "share_memory_with_cloud")
                ?? OptionalBoolean(payload, "cloud_memory_consent")
                ?? current.ShareMemoryWithCloud,
            OptionalBoolean(payload, "kilo_reasoning_enabled")
                ?? current.KiloReasoningEnabled,
            checkpoint,
            OptionalString(payload, "voice_label", 128) ?? current.VoiceLabel,
            voiceStyle,
            OptionalDouble(payload, "voice_speed", 0.5, 2.0) ?? current.VoiceSpeed,
            OptionalBoolean(payload, "auto_pace_enabled") ?? current.AutoPaceEnabled,
            OptionalBoolean(payload, "auto_read_replies") ?? current.AutoReadReplies,
            OptionalBoolean(payload, "show_subtitles") ?? current.ShowSubtitles);
    }

    private static IReadOnlyList<CredentialUpdate> BuildCredentialUpdates(
        string backend,
        JsonObject payload)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var openCode = OptionalSecret(payload, "opencode_api_key");
        var kilo = OptionalSecret(payload, "kilo_api_key");
        var gemini = OptionalSecret(payload, "gemini_vision_api_key");
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
            "api_variant",
            "kilo_reasoning_enabled",
            "checkpoint_path",
        };
        return runtimeFields.Any(payload.ContainsKey)
            || updates.Any(update => update.Account is OpenCodeAccount or KiloAccount);
    }

    private static string NormalizeBackend(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "gguf" or "llama.cpp" => BackendGguf,
            "opencode" or "opencode zen" => BackendOpenCode,
            "kilo" or "kilo gateway" => BackendKilo,
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
