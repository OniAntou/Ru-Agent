using System.Text.Json.Nodes;
using RuBackend.Worker;

namespace RuBackend.Business.Settings;

public sealed class LoopbackRuntimeSettingsLoader : IRuntimeSettingsLoader
{
    private readonly DeviceWorkerClientOptions _options;

    public LoopbackRuntimeSettingsLoader(DeviceWorkerClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async Task<string> LoadAsync(
        SettingsSnapshot candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await using var connection = new WorkerConnection(_options);
        var hello = await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!hello.Capabilities.Contains("runtime"))
        {
            throw new WorkerProtocolException("unsupported_operation");
        }

        var request = new WorkerEnvelope(
            DeviceWorkerClientOptions.ProtocolVersion,
            $"runtime-{Guid.NewGuid():N}",
            WorkerMessageTypes.RuntimeLoad,
            null,
            new JsonObject
            {
                ["backend"] = candidate.Backend,
                ["model"] = candidate.Model,
                ["opencode_model"] = candidate.OpenCodeModel,
                ["kilo_model"] = candidate.KiloModel,
                ["ollama_model"] = candidate.OllamaModel,
                ["lmstudio_model"] = candidate.LmStudioModel,
                ["custom_model"] = candidate.CustomModel,
                ["ollama_url"] = candidate.OllamaUrl,
                ["lmstudio_url"] = candidate.LmStudioUrl,
                ["custom_url"] = candidate.CustomUrl,
                ["api_variant"] = candidate.ApiVariant,
                ["kilo_reasoning_enabled"] = candidate.KiloReasoningEnabled,
                ["voice_label"] = candidate.VoiceLabel,
                ["voice_style_id"] = candidate.VoiceStyleId,
                ["voice_speed"] = candidate.VoiceSpeed,
                ["auto_pace_enabled"] = candidate.AutoPaceEnabled,
                ["auto_read_replies"] = candidate.AutoReadReplies,
                ["show_subtitles"] = candidate.ShowSubtitles,
                ["subtitle_color"] = candidate.SubtitleColor,
                ["subtitle_size"] = candidate.SubtitleSize,
                ["subtitle_duration"] = candidate.SubtitleDuration,
                ["subtitle_pos_mode"] = candidate.SubtitlePosMode,
                ["subtitle_pos_x"] = candidate.SubtitlePosX,
                ["subtitle_pos_y"] = candidate.SubtitlePosY,
            });

        await foreach (var response in connection.SendRequestAsync(request, cancellationToken)
            .ConfigureAwait(false))
        {
            if (response.Type == WorkerMessageTypes.Error)
            {
                throw new WorkerProtocolException(ReadErrorCode(response.Payload));
            }

            if (response.Type != WorkerMessageTypes.RuntimeState
                || !string.Equals(
                    ReadOptionalString(response.Payload, "status"),
                    "success",
                    StringComparison.Ordinal))
            {
                throw new WorkerProtocolException("runtime_load_failed");
            }

            return ReadOptionalString(response.Payload, "message") is { Length: > 0 } message
                ? message
                : "Runtime loaded.";
        }

        throw new WorkerProtocolException("missing_response");
    }

    private static string ReadErrorCode(JsonObject payload) =>
        ReadOptionalString(payload, "code") is { Length: > 0 } code
            ? code
            : "runtime_load_failed";

    private static string? ReadOptionalString(JsonObject payload, string key) =>
        payload[key] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;
}
