using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Subagents;

namespace RuBackend.Harness;

public sealed partial class AgentV1Service
{
    private Task<bool> IsInternalSubagentRunAsync(RunId runId, CancellationToken ct) =>
        new SqliteSubagentStore(_options, _payloadStore).IsInternalRunAsync(runId, ct);

    private Task<bool> IsInternalSubagentSessionAsync(string sessionId, CancellationToken ct) =>
        new SqliteSubagentStore(_options, _payloadStore).IsInternalSessionAsync(new(sessionId), ct);
}
