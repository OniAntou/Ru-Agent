using System.Text.Json.Nodes;

namespace RuBackend.Business.Settings;

public sealed record SettingsSnapshot(
    JsonObject Raw,
    string Backend,
    string Model,
    string OpenCodeModel,
    string KiloModel,
    string ApiVariant,
    int MaxTokens,
    double Temperature,
    int TopK,
    bool AllowWebSearch,
    bool EnableSelfLearning,
    bool ShareMemoryWithCloud,
    bool KiloReasoningEnabled,
    string CheckpointPath,
    string VoiceLabel,
    string VoiceStyleId,
    double VoiceSpeed,
    bool AutoPaceEnabled,
    bool AutoReadReplies,
    bool ShowSubtitles,
    string OllamaModel,
    string LmStudioModel,
    string CustomModel,
    string OllamaUrl,
    string LmStudioUrl,
    string CustomUrl);
