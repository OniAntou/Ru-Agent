using System.Text.Json.Serialization;

namespace RuBackend.Business.Sessions;

public sealed record SessionMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed record SessionDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("messages")] IReadOnlyList<SessionMessage> Messages);

public sealed record SessionSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount);

public sealed record ConversationTurn(string Prompt, string Response);

public interface ISessionStore
{
    Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken);

    Task<SessionDocument?> GetAsync(string sessionId, CancellationToken cancellationToken);

    Task<string?> CreateAsync(CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken);

    Task<bool> AppendAsync(
        string sessionId,
        IReadOnlyList<SessionMessage> messages,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ConversationTurn>> GetContextTurnsAsync(
        string sessionId,
        int maxTurns,
        CancellationToken cancellationToken);
}
