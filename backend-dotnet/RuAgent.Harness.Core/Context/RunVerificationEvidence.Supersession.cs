using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuAgent.Harness.Core.Context;

public sealed partial class RunVerificationEvidence
{
    // Keep original evidence, including stale status, for replay and diagnostics.
    // Evaluate replacement only after authoritative freshness has been applied:
    // a later mutation or unknown freshness must reopen the original obligation.
    private IEnumerable<VerificationCheckEvidence> OutstandingChecks()
    {
        var replaced = Checks.Where(check => check.IsVerification && check.ExpectedExitCode == 0
                && check.Status == VerificationCheckStatus.Passed && !check.InvalidatedDuringExecution)
            .SelectMany(check => check.SupersededCheckIdentities ?? []).ToHashSet(StringComparer.Ordinal);
        return Checks.Where(check => VerificationObligationPolicy.IsOutstanding(check, replaced));
    }

    private static bool CanBeSuperseded(VerificationCheckEvidence check) => VerificationObligationPolicy.CanBeSuperseded(check);

    private static IReadOnlyList<string>? SelectSupersededChecks(JsonElement args, TerminalMetadata metadata,
        VerificationEvidenceBuffer evidence, string identity)
    {
        if (StringArg(args, "purpose") != "verification" || metadata.WorkingDirectoryIdentity is null
            || !args.TryGetProperty("supersedes", out var targets)) return null;
        var arguments = JsonNode.Parse(args.GetRawText())!.AsObject();
        if (!TerminalWorkflowIntent.HasValidSupersedes(arguments)) return null;
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets.EnumerateArray())
        {
            var reference = target.GetString()!;
            var check = evidence.Find(reference);
            if (check is null)
            {
                // Providers may reuse call IDs between steps. Such an alias
                // cannot identify an obligation; the stable check ID still can.
                var aliases = evidence.Checks.Where(c => c.ProviderCallId == reference).Take(2).ToArray();
                if (aliases.Length != 1) continue;
                check = aliases[0];
            }
            if (check.Identity != identity && check.IsVerification && check.ExpectedExitCode == 0
                && check.ObservedExitCode == 0 && check.Status is VerificationCheckStatus.Passed or VerificationCheckStatus.Stale
                && !check.InvalidatedDuringExecution && string.Equals(check.WorkingDirectoryIdentity,
                    metadata.WorkingDirectoryIdentity, StringComparison.Ordinal))
                selected.Add(check.Identity);
        }
        return Array.AsReadOnly(selected.ToArray());
    }
}
