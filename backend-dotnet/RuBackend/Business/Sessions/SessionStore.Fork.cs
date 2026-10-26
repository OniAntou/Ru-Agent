using System.Security.Cryptography;
using System.Text.Json;

namespace RuBackend.Business.Sessions;

public sealed record SessionForkResult(SessionDocument? Document, string? ErrorCode);

public sealed partial class SessionStore
{
    public static string HistoryRevision(SessionDocument document) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(document.Messages))).ToLowerInvariant();

    public async Task<SessionForkResult> ForkAsync(string sourceId, int keepMessages, string revision,
        CancellationToken cancellationToken)
    {
        if (!IsValidSessionId(sourceId) || !IsSha256(revision)) return new(null, "invalid_fork_request");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var source = await LoadUnlockedAsync(sourceId, cancellationToken, connection, transaction).ConfigureAwait(false);
            if (source is null) return new(null, "session_not_found");
            if (HistoryRevision(source) != revision) return new(null, "history_stale");
            // Only whole-turn boundaries are accepted, including before the first turn.
            if (keepMessages < 0 || keepMessages > source.Messages.Count
                || (keepMessages < source.Messages.Count && source.Messages[keepMessages].Role != "user"))
                return new(null, "invalid_fork_boundary");
            var now = _clock.GetUtcNow().ToUniversalTime();
            var messages = source.Messages.Take(keepMessages).ToArray();
            // Rebuild only the retained source ledger. Runs, grants, approvals and work state
            // belong to the original session and are never transferred to this new identity.
            var child = new SessionDocument(Guid.NewGuid().ToString("N"), source.Title, now, now, messages,
                ParentSessionId: source.Id,
                ForkedFromMessageId: messages.LastOrDefault()?.SourceMessageId, ClientSource: "cli");
            child = child with { SourceMessageLedger = BuildSourceLedger(child) };
            await WriteUnlockedAsync(child, connection, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return new(child, null);
        }
        finally { _gate.Release(); }
    }
}
