using System.Text.Json.Nodes;

namespace RuBackend.Business.Settings;

public sealed record SettingsSnapshot(
    JsonObject Raw,
    string Backend,
    string Model,
    string CustomModel,
    string CustomUrl,
    string OpenAiModel,
    string AnthropicModel,
    double CompactionTriggerRatio = SettingsSnapshot.DefaultCompactionTriggerRatio,
    double CompactionTargetRatio = SettingsSnapshot.DefaultCompactionTargetRatio,
    string ProviderId = "")
{
    // Mirror of ContextBudgetPolicyOptions.DefaultNormalTriggerRatio /
    // DefaultNormalTargetRatio in Harness.Core, which is the single source for
    // policy defaults. Kept as local consts so record default args stay
    // self-contained; update both sides together.
    public const double DefaultCompactionTriggerRatio = 0.80;
    public const double DefaultCompactionTargetRatio = 0.62;
}
