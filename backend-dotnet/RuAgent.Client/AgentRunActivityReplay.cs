namespace RuAgent.Client;

/// <summary>One ordered replay unit: narration text, or a pointer to an activity row.</summary>
public sealed record AgentRunTranscriptEntry(long Sequence, string Kind, string? Text, string? ActivityId);

/// <summary>A run's durable activity rows and the narration transcript that interleaves them.</summary>
public sealed record AgentRunActivityReplay(
    IReadOnlyList<AgentActivityModel> Activities,
    IReadOnlyList<AgentRunTranscriptEntry> Transcript);
