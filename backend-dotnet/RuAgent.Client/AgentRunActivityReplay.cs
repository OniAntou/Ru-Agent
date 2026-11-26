namespace RuAgent.Client;

/// <summary>One ordered replay unit: narration text, or a pointer to an activity row.</summary>
public sealed record AgentRunTranscriptEntry(long Sequence, string Kind, string? Text, string? ActivityId);

/// <summary>
/// One page of a run's durable activity rows and the narration transcript that interleaves
/// them. While <see cref="HasMore"/> is true, <see cref="NextBeforeSequence"/> reads the page
/// just older; <see cref="OlderCount"/> is how many older rows remain, about one per row. A host
/// that predates paging answers one page with none of the three.
/// </summary>
public sealed record AgentRunActivityReplay(
    IReadOnlyList<AgentActivityModel> Activities,
    IReadOnlyList<AgentRunTranscriptEntry> Transcript,
    bool HasMore = false,
    long? NextBeforeSequence = null,
    int OlderCount = 0);
