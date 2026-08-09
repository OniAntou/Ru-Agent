namespace RuBackend.Memory;

public interface IMemoryPolicy
{
    IReadOnlyList<MemoryCandidate> ExtractCandidates(string prompt, string response);
    bool IsSensitiveOrUnsafe(string text);
    string StripInternalMetadata(string text);
}
