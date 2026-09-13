namespace RuAgent.Runtime.Core.Contracts;

public static class RuntimeLimits
{
    public const int MaxVoiceTextBytes = 12_000;
    public const int MaxVoiceAudioBytes = 32 * 1024 * 1024;
    public const int MaxSpeechAudioBytes = 5_625_000;
    public const int MaxModelNameChars = 128;
}
