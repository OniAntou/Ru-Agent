using RuAgent.Broker.Subagents.Integration;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Runtime;

public sealed partial class BrokerExecutionRuntime
{
    /// <summary>Trusted inspection seam. Never issues an authorization token or changes journal receipts.</summary>
    public Task<SubagentApplyInspection> InspectSubagentApplyAsync(string sessionId, string runId,
        SubagentChangeSet changes, SubagentApplyJournalState journal, CancellationToken ct) =>
        UnderLifecycleTaskAsync(async () =>
        {
            var root = SubagentApplyRoot(sessionId, runId);
            using var operation = _runCancellations.Register(runId, ct);
            using var handle = DirectoryHandleLease.Open(root.CanonicalRoot, requireCanonicalPath: true);
            var preparation = SubagentApplyPreparation.Create(root, changes);
            var inspection = await SubagentApplyInspector.InspectAsync(root, handle.Identity, preparation, journal,
                operation.Token).ConfigureAwait(false);
            operation.Token.ThrowIfCancellationRequested();
            _ = SubagentApplyRoot(sessionId, runId);
            return inspection;
        }, ct);
}
