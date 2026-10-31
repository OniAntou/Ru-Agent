using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness;

public sealed record AgentServiceResult(int StatusCode, object? Body);

/// <summary>
/// Stable pure projections retained for the SSE writer and deterministic tests.
/// HTTP endpoints resolve the resource services directly.
/// </summary>
public static class AgentV1Service
{
    public static bool IsVisionModel(string? model) => ModelImage.IsVisionModel(model);

    public static string MapHistoryWaitKind(HistoryWaitKind? kind) => AgentV1Shared.MapHistoryWaitKind(kind);

    public static string MapRunState(HarnessRunStatus status) => AgentV1Shared.MapRunState(status);

    internal static string MapRunState(HarnessRunStatus status, bool isWaitingInput) =>
        AgentV1Shared.MapRunState(status, isWaitingInput);

    internal static void ClearHostApprovalsForTests() => AgentApprovalsService.ClearHostApprovalsForTests();

    internal static void ClearHostInputsForTests() => AgentInputsService.ClearHostInputsForTests();

    internal static (AgentRun NextRun, HarnessEventIntent[] Events) PlanContinueCommit(
        AgentRun run,
        HarnessRunStatus nextStatus,
        bool clearGate,
        IIdGenerator ids,
        DateTimeOffset now) => AgentRunLifecycleService.PlanContinueCommit(run, nextStatus, clearGate, ids, now);
}
