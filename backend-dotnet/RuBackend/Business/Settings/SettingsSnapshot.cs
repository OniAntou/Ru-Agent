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
    string CustomModel,
    string CustomUrl,
    string OpenAiModel,
    string AnthropicModel,
    double CompactionTriggerRatio = SettingsSnapshot.DefaultCompactionTriggerRatio,
    double CompactionTargetRatio = SettingsSnapshot.DefaultCompactionTargetRatio)
{
    public const double DefaultCompactionTriggerRatio = 0.80;
    public const double DefaultCompactionTargetRatio = 0.62;
}
