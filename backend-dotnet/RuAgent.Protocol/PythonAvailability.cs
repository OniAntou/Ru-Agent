namespace RuAgent.Protocol;

/// <summary>
/// File-existence-only compatibility check for a Python interpreter on PATH.
/// Nothing is spawned. The shared bounded capability probe caches process-wide answers.
/// </summary>
public static class PythonAvailability
{
    public const int MaxSearchDirectories = SkillCapabilityAvailability.MaxSearchDirectories;
    public static bool IsAvailable => SkillCapabilityAvailability.IsAvailable("python");
    public static bool Probe(IEnumerable<string> searchDirectories) => SkillCapabilityAvailability.Probe("python", searchDirectories);
}
