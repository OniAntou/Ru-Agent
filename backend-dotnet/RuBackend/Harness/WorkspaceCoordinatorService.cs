using RuAgent.Harness.Core;

namespace RuBackend.Harness;

public sealed record WorkspaceClaimEvaluation(
    bool Accepted,
    string? ErrorCode,
    IReadOnlyList<string> ConflictingPaths);

public sealed class WorkspaceCoordinatorService
{
    public static WorkspaceClaimEvaluation EvaluateClaims(
        IReadOnlyList<WorkspacePathClaim> existingClaims,
        IReadOnlyList<WorkspacePathClaim> incomingClaims)
    {
        ArgumentNullException.ThrowIfNull(existingClaims);
        ArgumentNullException.ThrowIfNull(incomingClaims);
        var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var incoming in incomingClaims)
        {
            foreach (var existing in existingClaims)
            {
                if (WorkspaceConcurrencyDecision.EvaluateClaim(existing, incoming) == WorkspaceClaimDecision.BinaryConflict)
                {
                    conflicts.Add(incoming.Normalize().NormalizedPath);
                }
            }
        }

        var paths = conflicts.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Length == 0
            ? new WorkspaceClaimEvaluation(true, null, paths)
            : new WorkspaceClaimEvaluation(false, "concurrent_binary_conflict", paths);
    }
}
