using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents.Integration;

internal static class SubagentApplyInspector
{
    public static async Task<SubagentApplyInspection> InspectAsync(BrokerRunLease root, string rootIdentity,
        SubagentApplyPreparation preparation, SubagentApplyJournalState journal, CancellationToken ct)
    {
        ValidateJournal(preparation, journal);
        var observed = new List<SubagentApplyFileObservation>(preparation.Changes.Files.Count);
        long bytes = 0;
        for (var index = 0; index < preparation.Changes.Files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = preparation.Changes.Files[index];
            var context = new PolicyContextFactory(root).Create("read", "1.0.0", new JsonObject { ["path"] = file.RelativePath });
            if (SubagentApplyPreparation.Evaluate(root, context).Decision != "allow")
                throw new AgentProtocolException("subagent_inspection_read_denied");
            var path = context.Target!.CanonicalPath;
            string? current;
            // Ancestor identity is held while opening the single-link file. A
            // directory in place of a file or a non-canonical/reparse ancestor
            // cannot be reported as absence. No parent directory is created.
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory))
            {
                current = null;
            }
            else
            {
                using var parent = DirectoryHandleLease.Open(directory, requireCanonicalPath: true);
                if (Directory.Exists(path)) throw new AgentProtocolException("subagent_inspection_target_invalid");
                try
                {
                    using var stream = HardLinkGuard.OpenRead(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.SequentialScan | FileOptions.Asynchronous);
                    bytes = checked(bytes + stream.Length);
                    if (stream.Length > FileMutationGuard.MaxRollbackBytes || bytes > SubagentLimits.MaxSnapshotBytes)
                        throw new AgentProtocolException("subagent_inspection_too_large");
                    current = await FileHashService.ComputeStreamHashAsync(stream, ct).ConfigureAwait(false);
                }
                catch (FileNotFoundException) { current = FileHashService.Missing; }
            }
            var match = current is null ? "unavailable"
                : current == file.BaseHash ? "base" : current == file.ProposedHash ? "proposed" : "diverged";
            observed.Add(new(index, file.RelativePath, current, match, index < journal.Files.Count,
                index < journal.Files.Count && journal.Files[index].Receipted));
        }
        var hash = FileHashService.ComputeBytesHash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            preparation.Fingerprint, rootIdentity, root.BindingVersion, root.SnapshotHash,
            journal.Outcome, Files = observed,
        }));
        return new(preparation.Fingerprint, hash, observed.AsReadOnly());
    }

    private static void ValidateJournal(SubagentApplyPreparation preparation, SubagentApplyJournalState journal)
    {
        if (journal.Fingerprint != preparation.Fingerprint || !Enum.IsDefined(journal.Outcome.Status)
            || journal.Files.Count > preparation.Changes.Files.Count || journal.Outcome.AppliedFiles < 0
            || journal.Outcome.AppliedFiles > preparation.Changes.Files.Count)
            throw new AgentProtocolException("subagent_apply_journal_invalid");
        for (var index = 0; index < journal.Files.Count; index++)
        {
            var recorded = journal.Files[index];
            var frozen = preparation.Changes.Files[index];
            if (recorded.FileIndex != index || recorded.RelativePath != frozen.RelativePath
                || recorded.BaseHash != frozen.BaseHash || recorded.ProposedHash != frozen.ProposedHash || recorded.Mode != frozen.Mode)
                throw new AgentProtocolException("subagent_apply_journal_invalid");
        }
        var receipts = journal.Files.Count(f => f.Receipted);
        // Pending has no aggregate finish: per-file receipts may already be
        // durable while the operation's applied_files is still its initial zero.
        if (journal.Outcome.Status != SubagentApplyStatus.Pending && receipts > journal.Outcome.AppliedFiles
            || journal.Outcome.Status == SubagentApplyStatus.Applied
            && (receipts != preparation.Changes.Files.Count || receipts != journal.Outcome.AppliedFiles))
            throw new AgentProtocolException("subagent_apply_journal_invalid");
    }
}
