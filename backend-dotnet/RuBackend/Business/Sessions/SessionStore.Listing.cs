using System.Text.Json;

namespace RuBackend.Business.Sessions;

public sealed partial class SessionStore
{
    private const int MaximumListedSessions = 500;

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken) =>
        (await ListWithMetadataAsync(cancellationToken).ConfigureAwait(false)).Sessions;

    public async Task<SessionListResult> ListWithMetadataAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT summary FROM business_sessions ORDER BY updated_at_utc DESC, session_id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", MaximumListedSessions + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var summaries = new List<SessionSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            summaries.Add(JsonSerializer.Deserialize<SessionSummary>(reader.GetString(0))
                ?? throw new InvalidDataException("session_summary_invalid"));
        }
        return new(summaries.Take(MaximumListedSessions).ToArray(), summaries.Count > MaximumListedSessions);
    }

    private static SessionSummary ToSummary(SessionDocument document) => new(
        document.Id, document.Title, document.CreatedAt, document.UpdatedAt, document.Messages.Count,
        document.ParentSessionId, document.ForkedFromMessageId, LastAgentActivityAt: document.LastAgentActivityAt);
}
