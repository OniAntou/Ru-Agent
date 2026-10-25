using System.Text.Json.Nodes;

namespace RuAgent.Protocol.Programs;

public sealed record ProgramJournalWrite(bool Created, JsonObject Value);

/// <summary>Storage and accounting only. Implementations cannot authorize or execute tools.</summary>
public interface IProgramJournal
{
    Task<JsonObject?> ReadAsync(string runId, string key, CancellationToken ct);
    Task<ProgramJournalWrite> CreateAsync(string runId, string key, JsonObject value, CancellationToken ct);
    Task<bool> ReserveToolAsync(string runId, string key, CancellationToken ct);
}
