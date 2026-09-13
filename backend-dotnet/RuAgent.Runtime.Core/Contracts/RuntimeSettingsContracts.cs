namespace RuAgent.Runtime.Core.Contracts;

/// <summary>
/// Non-secret runtime and voice settings shared between the public host and
/// the managed device worker. Provider credentials never belong in this
/// snapshot.
/// </summary>
public sealed record RuntimeSettingsSnapshot(
    string Backend,
    string ModelName,
    Uri? BaseUrl,
    string ApiVariant,
    bool KiloReasoningEnabled)
{
    public VoiceSettingsSnapshot Voice { get; init; } = new();
}

public sealed record VoiceSettingsSnapshot(
    string VoiceLabel = "Ru (OmniVoice)",
    string VoiceStyleId = "original",
    double VoiceSpeed = 1.0,
    bool AutoPaceEnabled = false,
    bool AutoReadReplies = true,
    bool ShowSubtitles = false,
    string SubtitleColor = "#FFFFFF",
    string SubtitleSize = "medium",
    double SubtitleDuration = 2.0,
    string SubtitlePosMode = "auto",
    int SubtitlePosX = -1,
    int SubtitlePosY = -1);
