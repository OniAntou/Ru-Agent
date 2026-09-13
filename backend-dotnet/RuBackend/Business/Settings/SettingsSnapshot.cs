using System.Text.Json.Nodes;

namespace RuBackend.Business.Settings;

public sealed record SettingsSnapshot(
    JsonObject Raw,
    string Backend,
    string Model,
    string OpenCodeModel,
    string KiloModel,
    string ApiVariant,
    bool KiloReasoningEnabled,
    string VoiceLabel,
    string VoiceStyleId,
    double VoiceSpeed,
    bool AutoPaceEnabled,
    bool AutoReadReplies,
    bool ShowSubtitles,
    string SubtitleColor,
    string SubtitleSize,
    double SubtitleDuration,
    string SubtitlePosMode,
    int SubtitlePosX,
    int SubtitlePosY,
    string CustomModel,
    string CustomUrl,
    string OpenAiModel,
    string AnthropicModel);
