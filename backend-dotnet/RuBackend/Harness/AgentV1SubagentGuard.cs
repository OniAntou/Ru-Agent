using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;

namespace RuBackend.Harness;

public sealed class AgentV1SubagentGuard
{
    private readonly HarnessSqliteOptions _options;
    private readonly HarnessPayloadStore _payloadStore;

    public AgentV1SubagentGuard(HarnessSqliteOptions options, HarnessPayloadStore payloadStore)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
    }

    public Task<bool> IsInternalRunAsync(RunId runId, CancellationToken ct) =>
        new SqliteSubagentStore(_options, _payloadStore).IsInternalRunAsync(runId, ct);

    public Task<bool> IsInternalSessionAsync(string sessionId, CancellationToken ct) =>
        new SqliteSubagentStore(_options, _payloadStore).IsInternalSessionAsync(new(sessionId), ct);
}
