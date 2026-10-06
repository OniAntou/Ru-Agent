using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public static class AgentRunEvidenceActivityMapper
{
    public static AgentActivityModel Compaction(string runId, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        return new AgentActivityModel(
            $"act_compaction_{runId}_{sequence}",
            AgentActivityKind.Compaction,
            AgentActivityState.Completed,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "context_compacted",
            null,
            sequence,
            null);
    }
}
