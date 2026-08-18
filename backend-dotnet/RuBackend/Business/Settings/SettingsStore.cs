using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuBackend.Business.Settings;

public interface ISettingsStore
{
    Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken);

    Task SaveAsync(SettingsSnapshot candidate, CancellationToken cancellationToken);
}

public sealed class SettingsStore : ISettingsStore
{
    private const string BackendOpenCode = "OpenCode Zen";
    private const string BackendKilo = "Kilo Gateway";
    private const string BackendOllama = "Ollama";
    private const string BackendLmStudio = "LM Studio";
    private const string BackendCustom = "Custom OpenAI";
    private const string OriginalVoiceStyle = "original";

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
    ];

    private readonly string _settingsFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _settingsFile = paths.SettingsFile;
    }

    public async Task<SettingsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        SettingsSnapshot candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Validate(candidate);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var persisted = candidate.Raw.DeepClone().AsObject();
            foreach (var field in CredentialFields)
            {
                persisted.Remove(field);
            }

            persisted["backend"] = candidate.Backend;
            persisted["model"] = candidate.Model;
            persisted["opencode_model"] = candidate.OpenCodeModel;
            persisted["kilo_model"] = candidate.KiloModel;
            persisted["api_variant"] = candidate.ApiVariant;
            persisted["max_tokens"] = candidate.MaxTokens;
            persisted["temperature"] = candidate.Temperature;
            persisted["top_k"] = candidate.TopK;
            persisted["allow_web_search"] = candidate.AllowWebSearch;
            persisted["enable_self_learning"] = candidate.EnableSelfLearning;
            persisted["share_memory_with_cloud"] = candidate.ShareMemoryWithCloud;
            persisted["kilo_reasoning_enabled"] = candidate.KiloReasoningEnabled;
            persisted["checkpoint_path"] = candidate.CheckpointPath;
            persisted["voice_label"] = candidate.VoiceLabel;
            persisted["voice_style_id"] = candidate.VoiceStyleId;
            persisted["voice_speed"] = candidate.VoiceSpeed;
            persisted["auto_pace_enabled"] = candidate.AutoPaceEnabled;
            persisted["auto_read_replies"] = candidate.AutoReadReplies;
            persisted["show_subtitles"] = candidate.ShowSubtitles;
            persisted["ollama_model"] = candidate.OllamaModel;
            persisted["lmstudio_model"] = candidate.LmStudioModel;
            persisted["custom_model"] = candidate.CustomModel;
            persisted["ollama_url"] = candidate.OllamaUrl;
            persisted["lmstudio_url"] = candidate.LmStudioUrl;
            persisted["custom_url"] = candidate.CustomUrl;

            await WriteAtomicAsync(persisted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SettingsSnapshot> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        JsonObject raw = [];
        if (File.Exists(_settingsFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_settingsFile, cancellationToken)
                    .ConfigureAwait(false);
                raw = JsonNode.Parse(json) as JsonObject ?? [];
            }
            catch (JsonException)
            {
                raw = [];
            }
            catch (IOException)
            {
                raw = [];
            }
            catch (UnauthorizedAccessException)
            {
                raw = [];
            }
        }

        foreach (var field in CredentialFields)
        {
            raw.Remove(field);
        }

        var rawBackend = ReadString(raw, "backend", string.Empty);
        var migratedFromGguf = rawBackend is "gguf" or "llama.cpp";
        var backend = NormalizeBackend(rawBackend);
        var model = ReadString(raw, "model", string.Empty);
        var openCodeModel = ReadString(raw, "opencode_model", string.Empty);
        var kiloModel = ReadString(raw, "kilo_model", string.Empty);
        if (backend == BackendOpenCode && string.IsNullOrWhiteSpace(openCodeModel) && !migratedFromGguf)
        {
            openCodeModel = model;
        }
        else if (backend == BackendKilo && string.IsNullOrWhiteSpace(kiloModel))
        {
            kiloModel = model;
        }

        return new SettingsSnapshot(
            raw.DeepClone().AsObject(),
            backend,
            model,
            openCodeModel,
            kiloModel,
            ReadString(raw, "api_variant", "Default"),
            (int)ReadBoundedNumber(raw, "max_tokens", 256, 1, 1024),
            ReadBoundedNumber(raw, "temperature", 0.7, 0.1, 2.0),
            (int)ReadBoundedNumber(raw, "top_k", 50, 1, 200),
            ReadBoolean(raw, "allow_web_search", true),
            ReadBoolean(raw, "enable_self_learning", true),
            ReadBoolean(raw, "share_memory_with_cloud", true),
            ReadBoolean(raw, "kilo_reasoning_enabled", false),
            ReadString(raw, "checkpoint_path", string.Empty),
            ReadString(raw, "voice_label", "Ru (OmniVoice)"),
            NormalizeVoiceStyle(ReadString(raw, "voice_style_id", OriginalVoiceStyle)),
            ReadBoundedNumber(raw, "voice_speed", 1.0, 0.5, 2.0),
            ReadBoolean(raw, "auto_pace_enabled", false),
            ReadBoolean(raw, "auto_read_replies", true),
            ReadBoolean(raw, "show_subtitles", false),
            ReadString(raw, "ollama_model", "qwen2.5:latest"),
            ReadString(raw, "lmstudio_model", "default"),
            ReadString(raw, "custom_model", "default"),
            ReadString(raw, "ollama_url", "http://localhost:11434/v1"),
            ReadString(raw, "lmstudio_url", "http://localhost:1234/v1"),
            ReadString(raw, "custom_url", "http://localhost:8000/v1"));
    }

    private async Task WriteAtomicAsync(
        JsonObject persisted,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(_settingsFile)
            ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(
            parent,
            $".{Path.GetFileName(_settingsFile)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions);
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, _settingsFile, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string NormalizeBackend(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "gguf" or "llama.cpp" => BackendOpenCode,
            "opencode" or "opencode zen" => BackendOpenCode,
            "kilo" or "kilo gateway" => BackendKilo,
            "ollama" => BackendOllama,
            "lmstudio" or "lm studio" => BackendLmStudio,
            "custom" or "custom openai" => BackendCustom,
            _ => BackendOpenCode,
        };
    }

    private static string NormalizeVoiceStyle(string value)
    {
        return string.Equals(value, OriginalVoiceStyle, StringComparison.Ordinal)
            ? OriginalVoiceStyle
            : OriginalVoiceStyle;
    }

    private static string ReadString(JsonObject raw, string key, string fallback)
    {
        return raw[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : fallback;
    }

    private static bool ReadBoolean(JsonObject raw, string key, bool fallback)
    {
        return raw[key] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : fallback;
    }

    private static double ReadBoundedNumber(
        JsonObject raw,
        string key,
        double fallback,
        double minimum,
        double maximum)
    {
        if (raw[key] is not JsonValue value || !TryReadNumber(value, out var result))
        {
            return fallback;
        }

        return Math.Clamp(result, minimum, maximum);
    }

    private static bool TryReadNumber(JsonValue value, out double result)
    {
        if (value.TryGetValue<int>(out var integer))
        {
            result = integer;
            return true;
        }

        if (value.TryGetValue<long>(out var longInteger))
        {
            result = longInteger;
            return true;
        }

        return value.TryGetValue<double>(out result) && double.IsFinite(result);
    }

    private static void Validate(SettingsSnapshot candidate)
    {
        ValidateLength(candidate.Backend, 64, nameof(candidate.Backend));
        ValidateLength(candidate.Model, 256, nameof(candidate.Model));
        ValidateLength(candidate.OpenCodeModel, 256, nameof(candidate.OpenCodeModel));
        ValidateLength(candidate.KiloModel, 256, nameof(candidate.KiloModel));
        ValidateLength(candidate.OllamaModel, 256, nameof(candidate.OllamaModel));
        ValidateLength(candidate.LmStudioModel, 256, nameof(candidate.LmStudioModel));
        ValidateLength(candidate.CustomModel, 256, nameof(candidate.CustomModel));
        ValidateLength(candidate.OllamaUrl, 1024, nameof(candidate.OllamaUrl));
        ValidateLength(candidate.LmStudioUrl, 1024, nameof(candidate.LmStudioUrl));
        ValidateLength(candidate.CustomUrl, 1024, nameof(candidate.CustomUrl));
        ValidateLength(candidate.ApiVariant, 64, nameof(candidate.ApiVariant));
        ValidateLength(candidate.CheckpointPath, 1024, nameof(candidate.CheckpointPath));
        ValidateLength(candidate.VoiceLabel, 128, nameof(candidate.VoiceLabel));

        if (candidate.MaxTokens is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate.MaxTokens));
        }

        if (!double.IsFinite(candidate.Temperature) || candidate.Temperature is < 0.1 or > 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate.Temperature));
        }

        if (candidate.TopK is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate.TopK));
        }

        if (!double.IsFinite(candidate.VoiceSpeed) || candidate.VoiceSpeed is < 0.5 or > 2.0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidate.VoiceSpeed));
        }

        if (!string.Equals(candidate.VoiceStyleId, OriginalVoiceStyle, StringComparison.Ordinal))
        {
            throw new ArgumentException("Unsupported voice style.", nameof(candidate.VoiceStyleId));
        }
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
