namespace RuAgent.Protocol.Harness;

/// <summary>
/// The create-run API's input bounds, shared by the Host and every client so a
/// composed prompt is checked against the same number the Host enforces.
/// </summary>
public static class AgentRunLimits
{
    public const int MaxPromptChars = 16_000;
}
