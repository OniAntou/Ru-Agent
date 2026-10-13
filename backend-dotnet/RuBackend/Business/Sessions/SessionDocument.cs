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
    [property: JsonPropertyName("client_source")] string? ClientSource = null);

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

public sealed record ConversationTurn(string Prompt, string Response);

public interface ISessionStore
{
    Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken);

    async Task<SessionListResult> ListWithMetadataAsync(CancellationToken cancellationToken)
    {
        var sessions = await ListAsync(cancellationToken).ConfigureAwait(false);
        return new SessionListResult(sessions, IsTruncated: false);
    }

    Task<SessionDocument?> GetAsync(string sessionId, CancellationToken cancellationToken);

    Task<string?> CreateAsync(CancellationToken cancellationToken);

    Task<string?> CreateForClientAsync(string? clientSource, CancellationToken cancellationToken) =>
        CreateAsync(cancellationToken);

    Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken);

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
