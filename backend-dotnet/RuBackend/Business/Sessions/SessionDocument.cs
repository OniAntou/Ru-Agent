using System.Text.Json.Serialization;

namespace RuBackend.Business.Sessions;

public sealed record SessionMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("source_message_id")]
    string? SourceMessageId = null,
    [property: JsonPropertyName("source_run_id")]
    string? SourceRunId = null);

public enum SessionAppendOutcome
{
    Appended,
    AlreadyPresent,
    SessionNotFound,
    InvalidMessage,
    TransientFailure,
}

public sealed record SessionDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("messages")] IReadOnlyList<SessionMessage> Messages,
    [property: JsonPropertyName("source_message_ledger")]
    IReadOnlyDictionary<string, string>? SourceMessageLedger = null,
    [property: JsonPropertyName("parent_session_id")]
    string? ParentSessionId = null,
    [property: JsonPropertyName("forked_from_message_id")]
    string? ForkedFromMessageId = null,
    [property: JsonPropertyName("last_agent_activity_at")]
    DateTimeOffset? LastAgentActivityAt = null,
    [property: JsonPropertyName("client_source")] string? ClientSource = null,
    [property: JsonPropertyName("message_offset")] int MessageOffset = 0,
    [property: JsonPropertyName("total_message_count")] int? TotalMessageCount = null,
    [property: JsonPropertyName("history_epoch")] long HistoryEpoch = 0);

public sealed record SessionSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? ParentSessionId = null,
    string? ForkedFromMessageId = null,
    string? WorkspaceId = null,
    string? WorkspaceLabel = null,
    string? WorkspacePath = null,
    DateTimeOffset? LastAgentActivityAt = null,
    string? ClientSource = null);

public sealed record SessionListResult(
    IReadOnlyList<SessionSummary> Sessions,
    bool IsTruncated);

public sealed record SessionMessagePage(
    IReadOnlyList<SessionMessage> Messages,
    int StartIndex,
    int TotalMessages,
    string? NextCursor,
    long HistoryEpoch);

public sealed record SessionSummaryPage(
    IReadOnlyList<SessionSummary> Sessions,
    string? NextCursor);

public sealed record SessionMessageSearchHit(
    string SessionId,
    int MessageIndex,
    string Role,
    string Snippet);

public sealed class SessionHistoryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record ConversationTurn(string Prompt, string Response);

public interface ISessionStore
{
    Task<SessionForkResult> ForkAsync(string sourceId, int keepMessages, string revision, CancellationToken cancellationToken) =>
        Task.FromResult(new SessionForkResult(null, "session_fork_unsupported"));

    Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken);

    async Task<SessionListResult> ListWithMetadataAsync(CancellationToken cancellationToken)
    {
        var sessions = await ListAsync(cancellationToken).ConfigureAwait(false);
        return new SessionListResult(sessions, IsTruncated: false);
    }

    async Task<SessionSummaryPage> ListPageAsync(
        string? cursor, int limit, string? query, CancellationToken cancellationToken)
    {
        if (cursor is not null) throw new SessionHistoryException("session_history_cursor_unsupported");
        var result = await ListWithMetadataAsync(cancellationToken).ConfigureAwait(false);
        return new(result.Sessions.Take(limit).ToArray(), null);
    }

    Task<SessionDocument?> GetAsync(string sessionId, CancellationToken cancellationToken);

    async Task<SessionMessagePage?> GetMessagePageAsync(
        string sessionId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        if (cursor is not null) throw new SessionHistoryException("session_history_cursor_unsupported");
        var document = await GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (document is null) return null;
        if (limit < 1) throw new SessionHistoryException("session_history_limit_invalid");
        var messages = document.Messages.TakeLast(limit).ToArray();
        var start = document.MessageOffset + document.Messages.Count - messages.Length;
        return new(messages, start, document.TotalMessageCount ?? document.Messages.Count,
            null, document.HistoryEpoch);
    }

    Task<IReadOnlyList<SessionMessageSearchHit>> SearchMessagesAsync(
        string query, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SessionMessageSearchHit>>([]);

    Task<string?> CreateAsync(CancellationToken cancellationToken);

    Task<string?> CreateForClientAsync(string? clientSource, CancellationToken cancellationToken) =>
        CreateAsync(cancellationToken);

    Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken);

    // Legacy stores cannot prove that false means absence rather than failure.
    async Task<SessionDeleteOutcome> DeleteWithOutcomeAsync(string sessionId, CancellationToken cancellationToken) =>
        await DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ? SessionDeleteOutcome.Deleted : SessionDeleteOutcome.Failed;

    /// <summary>Removes an empty session atomically; preserves populated sessions.
    /// True means no orphaned empty row remains, including an already missing row.</summary>
    Task<bool> CleanupEmptyAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    Task<bool> AppendAsync(
        string sessionId,
        IReadOnlyList<SessionMessage> messages,
        CancellationToken cancellationToken);

    Task<SessionAppendOutcome> AppendIfAbsentAsync(
        string sessionId,
        SessionMessage message,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(
        string sessionId,
        int maxTurns,
        CancellationToken cancellationToken);

    Task<SessionDocument?> RewindAsync(
        string sessionId,
        int toMessageIndex,
        CancellationToken cancellationToken) => Task.FromResult<SessionDocument?>(null);
}
