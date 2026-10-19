using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Files;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Broker.Subagents.Integration;

public sealed class SubagentApplyAdapter(ISubagentApplyJournal journal)
{
    public Task<SubagentApplyOutcome> ApplyAsync(BrokerRunLease root, SubagentChangeSet changes, CancellationToken ct) =>
        ApplyAsync(root, SubagentApplyPreparation.Create(root, changes), false, null, ct);

    internal async Task<SubagentApplyOutcome> ApplyAsync(BrokerRunLease root, SubagentApplyPreparation preparation,
        bool approved, Func<Action, CancellationToken, Task>? guardedWrite, CancellationToken ct)
    {
        var changes = preparation.Changes;
        var files = changes.Files;
        ct.ThrowIfCancellationRequested();
        foreach (var file in files)
            _ = Resolve(root, file, approved);
        var previous = await journal.BeginAsync(changes, preparation.Fingerprint, ct).ConfigureAwait(false);
        if (previous is not null) return previous.Status == SubagentApplyStatus.Pending
            ? new(SubagentApplyStatus.Unknown, previous.AppliedFiles, "subagent_apply_reconciliation_required") : previous;

        var applied = 0;
        try
        {
            foreach (var file in files)
            {
                var path = Resolve(root, file, approved);
                if (FileHashService.ComputeFileHash(path) != file.BaseHash)
                    return await Finish(new(SubagentApplyStatus.Conflict, 0, "subagent_integration_conflict")).ConfigureAwait(false);
                // Validate every target's policy and adapter size before the first write.
                if (File.Exists(path) && new FileInfo(path).Length > FileMutationGuard.MaxRollbackBytes)
                    throw new AgentProtocolException("subagent_apply_file_too_large");
            }
            for (var index = 0; index < files.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[index];
                await JournalStep(() => journal.RecordFileIntentAsync(changes, index, ct)).ConfigureAwait(false);
                void Write()
                {
                    var path = Resolve(root, file, approved);
                    if (file.ProposedHash == FileHashService.Missing) FileMutationGuard.DeleteIfHashMatches(path, file.BaseHash);
                    else FileMutationGuard.ReplaceIfHashMatches(path, file.Content.ToArray(), file.BaseHash);
                }
                if (guardedWrite is null) Write();
                else await guardedWrite(Write, ct).ConfigureAwait(false);
                applied++;
                await JournalStep(() => journal.RecordFileReceiptAsync(changes, index, ct)).ConfigureAwait(false);
            }
            return await Finish(new(SubagentApplyStatus.Applied, applied, "subagent_applied")).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException error) when (error.Message == "workspace_changed")
        {
            return await Finish(new(applied == 0 ? SubagentApplyStatus.Conflict : SubagentApplyStatus.Partial,
                applied, "subagent_integration_conflict")).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException
            or AgentProtocolException or JournalStepException)
        {
            await Finish(new(SubagentApplyStatus.Unknown, applied, "subagent_apply_reconciliation_required")).ConfigureAwait(false);
            if (error is OperationCanceledException) throw;
            return new(SubagentApplyStatus.Unknown, applied, "subagent_apply_reconciliation_required");
        }

        async Task<SubagentApplyOutcome> Finish(SubagentApplyOutcome outcome)
        {
            try { await journal.FinishAsync(changes, outcome, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { throw new SubagentApplyInterruptedException(applied, error); }
            return outcome;
        }
    }

    private static async Task JournalStep(Func<Task> write)
    {
        try { await write().ConfigureAwait(false); }
        // Persistence implementations have different exception types. An intent/receipt
        // boundary failure cannot be reported as an ordinary no-effect tool failure.
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        { throw new JournalStepException(error); }
    }

    private sealed class JournalStepException(Exception inner) : Exception("subagent_apply_journal_failed", inner);

    private static string Resolve(BrokerRunLease root, SubagentFileChange change, bool approved)
    {
        var context = SubagentApplyPreparation.Resolve(root, change);
        var decision = SubagentApplyPreparation.Evaluate(root, context).Decision;
        if (decision == "deny")
            throw new AgentProtocolException("subagent_apply_denied");
        if (decision == "ask" && !approved)
            throw new AgentProtocolException("subagent_apply_approval_required");
        return context.Target!.CanonicalPath;
    }
}
