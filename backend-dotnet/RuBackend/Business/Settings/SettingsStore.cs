using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol.Providers;
using RuBackend.Providers;

namespace RuBackend.Business.Settings;

public interface ISettingsStore
{
    Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken);

    Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken);
}

public sealed partial class SettingsStore : ISettingsStore
{
    private const long MaximumSettingsFileBytes = 1 * 1024 * 1024;
    private const string DefaultBackend = "OpenAI";
    private const string BackendCustom = "Custom OpenAI";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly string[] CredentialFields =
    [
        "api_key",
        "opencode_api_key",
        "kilo_api_key",
        "gemini_vision_api_key",
        "custom_api_key",
        "openai_api_key",
        "anthropic_api_key",
    ];

    private static readonly string[] RetiredSettingsFields =
    [
        "subagents",
        "max_tokens",
        "max_new_tokens",
        "temperature",
        "top_k",
        "allow_web_search",
        "allow_online_search",
        "ollama_model",
        "ollama_url",
        "lmstudio_model",
        "lmstudio_url",
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
    ];

    private readonly string _settingsFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BusinessSqliteDatabase _database;
    private volatile bool _isMigrated;

    public SettingsStore(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _settingsFile = paths.SettingsFile;
        _database = new BusinessSqliteDatabase(paths);
    }

    public async Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(
        SettingsSnapshot candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Validate(candidate);

        // Fail-closed: an invalid pair can never be persisted. Normalize here
        // so storage always holds a usable pair even when the caller saved a
        // snapshot it built (or edited) without going through Read.
        var (triggerRatio, targetRatio) = NormalizeCompactionRatios(
            candidate.CompactionTriggerRatio,
            candidate.CompactionTargetRatio);
        candidate = candidate with
        {
            CompactionTriggerRatio = triggerRatio,
            CompactionTargetRatio = targetRatio,
        };

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var persisted = candidate.Raw.DeepClone().AsObject();
            foreach (var field in CredentialFields)
            {
                persisted.Remove(field);
            }
            foreach (var field in RetiredSettingsFields)
            {
                persisted.Remove(field);
            }

        persisted["backend"] = candidate.Backend;
        persisted["model"] = candidate.Model;
        if (candidate.Model.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            // A retired local-file model cannot run on the hosted path: leave
            // the field absent so the next read falls back to the migrated
            // provider field (opencode_model/kilo_model) instead of keeping a
            // dead filename, and clear the in-memory copy so callers that keep
            // using the candidate do not keep the dead value either.
            persisted.Remove("model");
            candidate = candidate with { Model = string.Empty };
        }
        persisted.Remove("opencode_model");
        persisted.Remove("kilo_model");
        persisted.Remove("api_variant");
        persisted.Remove("kilo_reasoning_enabled");
        persisted.Remove("checkpoint_path");
        persisted["custom_model"] = candidate.CustomModel;
            persisted["custom_url"] = candidate.CustomUrl;
            persisted["openai_model"] = candidate.OpenAiModel;
            persisted["anthropic_model"] = candidate.AnthropicModel;
            persisted["compaction_trigger_ratio"] = candidate.CompactionTriggerRatio;
            persisted["compaction_target_ratio"] = candidate.CompactionTargetRatio;
            persisted["provider_id"] = candidate.ProviderId;
            persisted["cloudflare_account_id"] = candidate.CloudflareAccountId;

            await WriteAtomicAsync(persisted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SettingsSnapshot> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM business_settings WHERE id = 1;";
        var document = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        var raw = document is null ? new JsonObject() : JsonNode.Parse(document)!.AsObject();

        foreach (var field in CredentialFields)
        {
            raw.Remove(field);
        }
        foreach (var field in RetiredSettingsFields)
        {
            raw.Remove(field);
        }

        var rawBackend = ReadString(raw, "backend", string.Empty);
        var backend = raw.ContainsKey("backend")
            ? NormalizeBackend(rawBackend)
            : DefaultBackend;
        var model = ReadString(raw, "model", string.Empty);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = ReadString(raw, "api_model", string.Empty);
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            // Retired built-ins kept their model in a provider field. Carry it
            // into the generic field so the migrated backend still has a model.
            model = backend switch
            {
                "models.dev:opencode" => ReadString(raw, "opencode_model", string.Empty),
                "models.dev:kilo" => ReadString(raw, "kilo_model", string.Empty),
                _ => string.Empty,
            };
        }

        var (compactionTriggerRatio, compactionTargetRatio) = ReadCompactionRatios(raw);

        return new SettingsSnapshot(
            raw.DeepClone().AsObject(),
            backend,
            model,
            ReadString(raw, "custom_model", "default"),
            ReadString(raw, "custom_url", "http://localhost:8000/v1"),
            ReadString(raw, "openai_model", string.Empty),
            ReadString(raw, "anthropic_model", string.Empty),
            compactionTriggerRatio,
            compactionTargetRatio,
            ReadString(raw, "provider_id", string.Empty),
            ReadString(raw, "cloudflare_account_id", string.Empty));
    }

    private async Task WriteAtomicAsync(JsonObject persisted, CancellationToken cancellationToken)
    {
        var document = JsonSerializer.Serialize(persisted, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(document) > MaximumSettingsFileBytes)
            throw new InvalidDataException("settings_file_too_large");
        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO business_settings(id, document) VALUES (1, $document) ON CONFLICT(id) DO UPDATE SET document = excluded.document;";
        command.Parameters.AddWithValue("$document", document);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (!await BusinessSqliteDatabase.HasMigratedAsync(connection, transaction, "settings", cancellationToken).ConfigureAwait(false))
            await BusinessSqliteDatabase.MarkMigratedAsync(connection, transaction, "settings", cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        _isMigrated = true;
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

        return string.Empty;
    }

    private static (double TriggerRatio, double TargetRatio) ReadCompactionRatios(JsonObject raw)
    {
        // Fail-closed: missing/invalid pairs fall back to defaults. Range and
        // finiteness checks reuse ContextBudgetPolicyOptions.IsValidRatio so the
        // rule lives in exactly one place; the 0 < target < trigger < 1
        // ordering is the settings-level addition (the policy validates each
        // ratio alone and allows exactly 1.0, which settings rejects).
        var trigger = ReadRatio(raw, "compaction_trigger_ratio", SettingsSnapshot.DefaultCompactionTriggerRatio);
        var target = ReadRatio(raw, "compaction_target_ratio", SettingsSnapshot.DefaultCompactionTargetRatio);
        if (trigger is null || target is null)
        {
            return (SettingsSnapshot.DefaultCompactionTriggerRatio, SettingsSnapshot.DefaultCompactionTargetRatio);
        }

        return NormalizeCompactionRatios(trigger.Value, target.Value);
    }

    /// <summary>
    /// Enforces the compaction spec (0 &lt; target &lt; trigger &lt; 1),
    /// fail-closed to defaults. Shared by the read path and <see cref="SaveAsync"/>
    /// so an invalid pair can never be returned or persisted.
    /// </summary>
    private static (double TriggerRatio, double TargetRatio) NormalizeCompactionRatios(
        double triggerRatio,
        double targetRatio)
    {
        if (!ContextBudgetPolicyOptions.IsValidRatio(triggerRatio)
            || !ContextBudgetPolicyOptions.IsValidRatio(targetRatio)
            || !(targetRatio < triggerRatio)
            || triggerRatio >= 1)
        {
            return (SettingsSnapshot.DefaultCompactionTriggerRatio, SettingsSnapshot.DefaultCompactionTargetRatio);
        }

        return (triggerRatio, targetRatio);
    }

    private static double? ReadRatio(JsonObject raw, string key, double fallback)
    {
        if (raw[key] is not JsonValue value)
        {
            return fallback;
        }

        if (!value.TryGetValue<double>(out var result))
        {
            return null;
        }

        if (!ContextBudgetPolicyOptions.IsValidRatio(result))
        {
            return null;
        }

        return result;
    }

    private static string ReadString(JsonObject raw, string key, string fallback)
    {
        return raw[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : fallback;
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream((int)Math.Min(maximumBytes, 81920));
        var buffer = new byte[(int)Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new InvalidDataException("settings_file_too_large");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static void Validate(SettingsSnapshot candidate)
    {
        ValidateLength(candidate.Backend, 64, nameof(candidate.Backend));
        ValidateLength(candidate.Model, 256, nameof(candidate.Model));
        ValidateLength(candidate.CustomModel, 256, nameof(candidate.CustomModel));
        ValidateLength(candidate.CustomUrl, 1024, nameof(candidate.CustomUrl));
        ValidateLength(candidate.OpenAiModel, 256, nameof(candidate.OpenAiModel));
        ValidateLength(candidate.AnthropicModel, 256, nameof(candidate.AnthropicModel));
        ValidateLength(candidate.ProviderId, 128, nameof(candidate.ProviderId));
        ValidateLength(candidate.CloudflareAccountId, 64, nameof(candidate.CloudflareAccountId));
    }

    private static void ValidateLength(string value, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length > maximum)
        {
            throw new ArgumentException($"Value exceeds {maximum} characters.", parameterName);
        }
    }
}
