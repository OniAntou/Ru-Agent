using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core.Workspace;

namespace RuAgent.Harness.Persistence.Workspace;

public readonly record struct WorkspaceTrustStoreResult(
    bool Accepted,
    bool Idempotent,
    string? ErrorCode,
    WorkspaceTrustRequest? Request);

public sealed class WorkspaceTrustRequestStore
{
    private const int CurrentTrustProfileVersion = 1;
    private const string ExplicitTrustSourceWireValue = "explicit";

    private readonly HarnessSqliteOptions _options;

    public WorkspaceTrustRequestStore(HarnessSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async Task<WorkspaceTrustStoreResult> ApplyAsync(
        WorkspaceTrustResponse response,
        WorkspaceIdentity currentIdentity,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(currentIdentity);
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var stored = await SqliteWorkspaceStore.LoadAsync(
                    connection,
                    transaction,
                    response.Workspace.WorkspaceId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stored is null || stored.TrustRequest.RequestId != response.RequestId)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Reject("trust_stale", stored?.TrustRequest);
            }

            var application = stored.TrustRequest.TryApply(response, currentIdentity, decidedAt);
            if (!application.Accepted)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return Reject(application.ErrorCode ?? "trust_stale", application.Next);
            }

            if (!application.Idempotent)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE agent_workspaces
                    SET trust_decision = $trust_decision,
                        trust_decided_at_utc = $decided_at,
                        trust_source = $trust_source,
                        trust_profile_version = $trust_profile_version,
                        updated_at_utc = $decided_at
                    WHERE workspace_id = $workspace_id
                      AND trust_request_id = $trust_request_id
                      AND trust_decision IS NULL
                    """;
                command.Parameters.AddWithValue("$trust_decision", (int)response.Decision);
                command.Parameters.AddWithValue(
                    "$trust_source",
                    response.Decision == WorkspaceTrustDecision.Approve
                        ? ExplicitTrustSourceWireValue
                        : DBNull.Value);
                command.Parameters.AddWithValue(
                    "$trust_profile_version",
                    CurrentTrustProfileVersion);
                command.Parameters.AddWithValue("$decided_at", decidedAt.ToString("O"));
                command.Parameters.AddWithValue("$workspace_id", response.Workspace.WorkspaceId.Value);
                command.Parameters.AddWithValue("$trust_request_id", response.RequestId.Value);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (affected != 1)
                {
                    throw new HarnessStoreException("trust_concurrency_conflict");
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new WorkspaceTrustStoreResult(
                Accepted: true,
                application.Idempotent,
                ErrorCode: null,
                application.Next);
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
            throw new HarnessStoreException("workspace_trust_store_failed", exception);
        }
    }

    private static WorkspaceTrustStoreResult Reject(
        string errorCode,
        WorkspaceTrustRequest? request) =>
        new(false, false, errorCode, request);
}
