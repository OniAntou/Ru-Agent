using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Policy;

namespace RuAgent.Harness.Persistence;

public sealed partial class SqliteHarnessEventStore : IHarnessEventStore
{
    private const int ProtocolVersion = 1;
    private readonly HarnessSqliteOptions _options;
    private readonly HarnessPayloadStore _payloadStore;
    private readonly SessionWorkStateStore _workStates;

    public SqliteHarnessEventStore(
        HarnessSqliteOptions options,
        HarnessPayloadStore payloadStore,
        SessionWorkStateStore workStates)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _workStates = workStates ?? throw new ArgumentNullException(nameof(workStates));
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
            if (await HarnessSqliteQueries.SessionDeletedAsync(
                    connection,
                    transaction,
                    request.Run.SessionId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return CreateRunResult.Conflict(request.Run, "session_deleted");
            }

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

            // Sessions share a workspace. Session ownership above still prevents
            // two active runs from racing within the same conversation; runtime
            // membership leases bound how many different sessions execute at once.

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
            await SaveInitialWorkStateAsync(connection, transaction, request, cancellationToken)
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

    public async Task<HarnessCommitResult> CommitAsync(        HarnessCommit commit,
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
            await VerifyOutboxPayloadsExistAsync(
                    connection,
                    transaction,
                    commit,
                    cancellationToken)
                .ConfigureAwait(false);
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
            if (commit.ContextCompaction is not null)
            {
                await HarnessSqliteWriteSet.InsertCompactionAsync(
                        connection,
                        transaction,
                        commit.ContextCompaction,
                        commit.ExpectedVersion,
                        commit.NextRun.UpdatedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            await HarnessSqliteWriteSet.InsertEventsAsync(
                    connection,
                    transaction,
                    commit.RunId,
                    checked(current.LastSequence + 1),
                    commit.Events,
                    commit.NextRun.UpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
            await UpdateWorkStateFromReceiptsAsync(connection, transaction, commit, current, cancellationToken)
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
                   provider_call_id,
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

    /// <summary>
    /// Outbox intents may deliver a payload created in this commit or one already
    /// durable from an earlier commit of the same run; anything else is rejected.
    /// </summary>
    private static async Task VerifyOutboxPayloadsExistAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HarnessCommit commit,
        CancellationToken cancellationToken)
    {
        if (commit.ConversationOutbox.Count == 0)
        {
            return;
        }

        var committedHere = commit.Payloads
            .Select(payload => payload.PayloadId.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var intent in commit.ConversationOutbox)
        {
            var payloadId = intent.PayloadId.Value;
            if (committedHere.Contains(payloadId))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(1)
                FROM agent_payloads
                WHERE run_id = $run_id AND payload_id = $payload_id
                """;
            command.Parameters.AddWithValue("$run_id", commit.RunId.Value);
            command.Parameters.AddWithValue("$payload_id", payloadId);
            var exists = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (exists == 0)
            {
                throw new HarnessStoreException("outbox_payload_missing");
            }
        }
    }
}
