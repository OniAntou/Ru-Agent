using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessPayloadStore
{
    /// <summary>Counts image facts only for user messages still visible in this session, including retained fork prefixes.</summary>
    public async Task<IReadOnlyDictionary<string, int>> ReadSessionImageCountsAsync(
        SessionId sessionId, IReadOnlyList<string> sourceRunIds, CancellationToken cancellationToken)
    {
        if (sourceRunIds.Count > 64) throw new ArgumentOutOfRangeException(nameof(sourceRunIds));
        var ids = sourceRunIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return new Dictionary<string, int>(StringComparer.Ordinal);
        await using var connection = await RequireOptions().OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var parameters = ids.Select((_, index) => "$source" + index).ToArray();
        command.CommandText = $"""
            SELECT p.run_id, COUNT(*) FROM agent_payloads p
            WHERE p.kind=$kind AND (p.audience & $audience) != 0
              AND p.run_id IN ({string.Join(',', parameters)})
              AND EXISTS (SELECT 1 FROM business_session_messages m
                  WHERE m.session_id=$session AND m.role='user' AND m.source_run_id=p.run_id)
            GROUP BY p.run_id
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value);
        command.Parameters.AddWithValue("$kind", (int)HarnessPayloadKind.UserImage);
        command.Parameters.AddWithValue("$audience", (int)HarnessPayloadAudience.ModelContext);
        for (int index = 0; index < ids.Length; index++) command.Parameters.AddWithValue(parameters[index], ids[index]);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0), reader.GetInt32(1));
        return result;
    }
}
