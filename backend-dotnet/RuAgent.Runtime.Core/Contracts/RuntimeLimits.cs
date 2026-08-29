namespace RuAgent.Runtime.Core.Contracts;

public static class RuntimeLimits
{
    public const int MaxVoiceTextBytes = 12_000;
    public const int MaxVoiceAudioBytes = 32 * 1024 * 1024;
    public const int MaxSpeechAudioBytes = 5_625_000;
    public const int MaxObservationImageBytes = 4 * 1024 * 1024;
    public const int MaxObservationPromptBytes = 16 * 1024;
    public const int MaxModelNameChars = 128;
    public const int MaxVtsCodes = 512;
    public const int MaxVtsCodeChars = 64;
    public const int MaxVtsActionCodes = 3;
    public const int MaxVisibleTextItems = 8;
}
