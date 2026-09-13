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
    string AnthropicModel);
