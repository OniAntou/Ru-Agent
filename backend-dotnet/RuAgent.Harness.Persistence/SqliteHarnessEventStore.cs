using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Policy;

namespace RuAgent.Harness.Persistence;

public sealed class SqliteHarnessEventStore : IHarnessEventStore
{
    private const int ProtocolVersion = 1;
    private readonly HarnessSqliteOptions _options;
    private readonly HarnessPayloadStore _payloadStore;

    public SqliteHarnessEventStore(
        HarnessSqliteOptions options,
        HarnessPayloadStore payloadStore)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
    }

    public Task<HarnessStoreHealth> GetHealthAsync(CancellationToken cancellationToken) =>
        HarnessSqliteSchema.InitializeAsync(_options, cancellationToken);

    public async Task<CreateRunResult> TryCreateRunAsync(
        NewAgentRun request,
        CancellationToken cancellationToken)
    {
        HarnessSqliteWriteSet.ValidateNewRun(request);
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var replay = await HarnessSqliteQueries.FindIdempotentRunAsync(
                    connection,
                    transaction,
                    request.Run.SessionId,
                    request.CreationIdentity.IdempotencyKeySha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return string.Equals(
                        replay.NormalizedRequestSha256,
                        request.CreationIdentity.NormalizedRequestSha256,
                        StringComparison.Ordinal)
                    ? CreateRunResult.AlreadyExists(replay.Run)
                    : CreateRunResult.Conflict(replay.Run, "idempotency_conflict");
            }

            var activeSession = await HarnessSqliteQueries.FindActiveSessionRunAsync(
                    connection,
                    transaction,
                    request.Run.SessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (activeSession is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CreateRunResult.Conflict(activeSession, "session_busy");
            }

            var activeWorkspace = await HarnessSqliteQueries.FindActiveWorkspaceRunAsync(
                    connection,
                    transaction,
                    request.Run.WorkspaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (activeWorkspace is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CreateRunResult.Conflict(activeWorkspace, "workspace_busy");
            }

            if (!await HarnessSqliteQueries.WorkspaceExistsAsync(
                    connection,
                    transaction,
                    request.Run.WorkspaceId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CreateRunResult.Conflict(request.Run, "workspace_not_found");
            }

            var permission = await AgentSessionPermissionStore.EnsureForRunInTransactionAsync(
                    connection,
                    transaction,
                    request.Run.SessionId,
                    request.Run.Preset,
                    request.CreationIdentity.SessionPresetVersion,
                    request.Run.CreatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            if (permission.Outcome is not SessionPermissionOutcome.Matched
                and not SessionPermissionOutcome.CreatedDefault)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CreateRunResult.Conflict(
                    request.Run,
                    permission.ErrorCode ?? "preset_mismatch");
            }

            await HarnessSqliteWriteSet.InsertRunAsync(
                    connection,
                    transaction,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            await _payloadStore.InsertAsync(
                    connection,
                    transaction,
                    request.Run.RunId,
                    request.Payloads,
                    request.Events,
                    firstEventSequence: 1,
                    request.Run.UpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            await HarnessSqliteWriteSet.InsertEventsAsync(
                    connection,
                    transaction,
                    request.Run.RunId,
                    firstSequence: 1,
                    request.Events,
                    request.Run.UpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            await HarnessSqliteWriteSet.InsertOutboxAsync(
                    connection,
                    transaction,
                    request.Run.RunId,
                    request.ConversationOutbox,
                    request.Run.UpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CreateRunResult.Created(request.Run);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("harness_create_failed", exception);
        }
    }

    public async Task<AgentRun?> GetRunAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        try
        {
            await using var connection = await _options.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return await HarnessSqliteQueries.GetRunAsync(
                    connection,
                    null,
                    runId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("harness_store_read_failed", exception);
        }
    }

    public async Task<HarnessCommitResult> CommitAsync(
        HarnessCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var current = await HarnessSqliteQueries.GetRunAsync(
                    connection,
                    transaction,
                    commit.RunId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return HarnessCommitResult.RunNotFound();
            }

            if (current.Version != commit.ExpectedVersion)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return HarnessCommitResult.VersionConflict(current);
            }

            HarnessSqliteWriteSet.ValidateCommit(current, commit);
            if (!await HarnessSqliteWriteSet.UpdateRunAsync(
                    connection,
                    transaction,
                    commit,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                throw new HarnessStoreException("harness_cas_failed");
            }

            await _payloadStore.InsertAsync(
                    connection,
                    transaction,
                    commit.RunId,
                    commit.Payloads,
                    commit.Events,
                    checked(current.LastSequence + 1),
                    commit.NextRun.UpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            await HarnessSqliteWriteSet.InsertEventsAsync(
                    connection,
                    transaction,
                    commit.RunId,
                    checked(current.LastSequence + 1),
                    commit.Events,
                    commit.NextRun.UpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            await HarnessSqliteWriteSet.InsertOutboxAsync(
                    connection,
                    transaction,
                    commit.RunId,
                    commit.ConversationOutbox,
                    commit.NextRun.UpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            await HarnessSqliteWriteSet.DeleteRunRulesIfTerminalAsync(
                    connection,
                    transaction,
                    commit.NextRun,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return HarnessCommitResult.Committed(commit.NextRun);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessStoreException exception) when (exception.Code is
            "run_projection_identity_changed" or "run_sequence_mismatch")
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("harness_commit_failed", exception);
        }
    }

    public async IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(
        RunId runId,
        long afterSequence,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        _ = runId.Value;
        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence));
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id,
                   run_id,
                   sequence,
                   kind,
                   occurred_at_utc,
                   payload_id,
                   model_step_id,
                   tool_call_id,
                   execution_id,
                   approval_id,
                   safe_count,
                   safe_code,
                   tool_outcome,
                   side_effects_possible
            FROM agent_events
            WHERE run_id = $run_id
              AND sequence > $after_sequence
            ORDER BY sequence
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        command.Parameters.AddWithValue("$after_sequence", afterSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return new HarnessEventEnvelope(
                ProtocolVersion,
                HarnessSqliteMapper.ReadEvent(reader));
        }
    }
}
