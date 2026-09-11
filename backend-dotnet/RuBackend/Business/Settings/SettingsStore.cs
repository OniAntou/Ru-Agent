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
    private const long MaximumSettingsFileBytes = 1 * 1024 * 1024;
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
        "openai_api_key",
        "anthropic_api_key",
    ];

    private static readonly string[] RetiredSettingsFields =
    [
        "max_tokens",
        "max_new_tokens",
        "temperature",
        "top_k",
        "allow_web_search",
        "allow_online_search",
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
            foreach (var field in RetiredSettingsFields)
            {
                persisted.Remove(field);
            }

            persisted["backend"] = candidate.Backend;
            persisted["model"] = candidate.Model;
            if (candidate.Model.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                persisted.Remove("model");
            }
            persisted["opencode_model"] = candidate.OpenCodeModel;
            persisted["kilo_model"] = candidate.KiloModel;
            persisted["api_variant"] = candidate.ApiVariant;
            persisted["kilo_reasoning_enabled"] = candidate.KiloReasoningEnabled;
            persisted.Remove("checkpoint_path");
            persisted["voice_label"] = candidate.VoiceLabel;
            persisted["voice_style_id"] = candidate.VoiceStyleId;
            persisted["voice_speed"] = candidate.VoiceSpeed;
            persisted["auto_pace_enabled"] = candidate.AutoPaceEnabled;
            persisted["auto_read_replies"] = candidate.AutoReadReplies;
            persisted["show_subtitles"] = candidate.ShowSubtitles;
            persisted["subtitle_color"] = candidate.SubtitleColor;
            persisted["subtitle_size"] = candidate.SubtitleSize;
            persisted["subtitle_duration"] = candidate.SubtitleDuration;
            persisted["subtitle_pos_mode"] = candidate.SubtitlePosMode;
            persisted["subtitle_pos_x"] = candidate.SubtitlePosX;
            persisted["subtitle_pos_y"] = candidate.SubtitlePosY;
            persisted["ollama_model"] = candidate.OllamaModel;
            persisted["lmstudio_model"] = candidate.LmStudioModel;
            persisted["custom_model"] = candidate.CustomModel;
            persisted["ollama_url"] = candidate.OllamaUrl;
            persisted["lmstudio_url"] = candidate.LmStudioUrl;
            persisted["custom_url"] = candidate.CustomUrl;
            persisted["openai_model"] = candidate.OpenAiModel;
            persisted["anthropic_model"] = candidate.AnthropicModel;

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
                if (new FileInfo(_settingsFile).Length <= MaximumSettingsFileBytes)
                {
                    var bytes = await ReadBoundedBytesAsync(_settingsFile, MaximumSettingsFileBytes, cancellationToken)
                        .ConfigureAwait(false);
                    raw = JsonNode.Parse(bytes) as JsonObject ?? [];
                }
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
            catch (InvalidDataException)
            {
                raw = [];
            }
        }

        foreach (var field in CredentialFields)
        {
            raw.Remove(field);
        }
        foreach (var field in RetiredSettingsFields)
        {
            raw.Remove(field);
        }

        var rawBackend = ReadString(raw, "backend", string.Empty);
        var migratedFromGguf = rawBackend.Trim().ToLowerInvariant() is "gguf" or "llama.cpp";
        var backend = raw.ContainsKey("backend")
            ? NormalizeBackend(rawBackend)
            : BackendOpenCode;
        var model = ReadString(raw, "model", string.Empty);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = ReadString(raw, "api_model", string.Empty);
        }
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
            ReadBoolean(raw, "kilo_reasoning_enabled", false),
            ReadString(raw, "voice_label", "Ru (OmniVoice)"),
            NormalizeVoiceStyle(ReadString(raw, "voice_style_id", OriginalVoiceStyle)),
            ReadBoundedNumber(raw, "voice_speed", 1.0, 0.5, 2.0),
            ReadBoolean(raw, "auto_pace_enabled", false),
            ReadBoolean(raw, "auto_read_replies", true),
            ReadBoolean(raw, "show_subtitles", false),
            ReadString(raw, "subtitle_color", "#FFFFFF"),
            ReadString(raw, "subtitle_size", "medium"),
            ReadBoundedNumber(raw, "subtitle_duration", 2.0, 1.0, 10.0),
            ReadString(raw, "subtitle_pos_mode", "auto"),
            ReadBoundedInt(raw, "subtitle_pos_x", -1, -1, 10000),
            ReadBoundedInt(raw, "subtitle_pos_y", -1, -1, 10000),
            ReadString(raw, "ollama_model", "qwen2.5:latest"),
            ReadString(raw, "lmstudio_model", "default"),
            ReadString(raw, "custom_model", "default"),
            ReadString(raw, "ollama_url", "http://localhost:11434/v1"),
            ReadString(raw, "lmstudio_url", "http://localhost:1234/v1"),
            ReadString(raw, "custom_url", "http://localhost:8000/v1"),
            ReadString(raw, "openai_model", string.Empty),
            ReadString(raw, "anthropic_model", string.Empty));
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
            if (bytes.LongLength > MaximumSettingsFileBytes)
            {
                throw new InvalidDataException("settings_file_too_large");
            }

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
            "opencode" or "opencode zen" or "opencode zen api" or "opencode api" => BackendOpenCode,
            "kilo" or "kilo gateway" or "kilo gateway api" or "kilo api" => BackendKilo,
            "ollama" or "ollama api" => BackendOllama,
            "lmstudio" or "lm studio" or "lm studio api" => BackendLmStudio,
            "custom" or "custom openai" or "custom openai api" => BackendCustom,
            "openai" or "openai api" => "OpenAI",
            "anthropic" or "anthropic claude" or "claude" => "Anthropic",
            _ => string.Empty,
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

    private static int ReadBoundedInt(
        JsonObject raw,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        if (raw[key] is not JsonValue value || !TryReadNumber(value, out var result))
        {
            return fallback;
        }

        return Math.Clamp((int)Math.Round(result), minimum, maximum);
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
        ValidateLength(candidate.OpenAiModel, 256, nameof(candidate.OpenAiModel));
        ValidateLength(candidate.AnthropicModel, 256, nameof(candidate.AnthropicModel));
        ValidateLength(candidate.ApiVariant, 64, nameof(candidate.ApiVariant));
        ValidateLength(candidate.VoiceLabel, 128, nameof(candidate.VoiceLabel));

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
