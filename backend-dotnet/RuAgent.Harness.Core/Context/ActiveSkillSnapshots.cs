using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

/// <summary>Projects bounded, run-local skill workflow references from durable receipts.</summary>
public static class ActiveSkillSnapshots
{
    public const string ExplicitSourceId = "base:invoked-skills";
    public const string ExplicitRunSourceId = "run:invoked-skills";
    public const int MaximumSkills = 4;
    public const int MaximumBodyBytes = 128 * 1024;
    private const int MaximumEncodedEntryBytes = 512 * 1024;
    private const int MaximumOmissionRecords = 16;
    private const string MoreOmissions = "additional skill workflows omitted (not individually listed)";
    private const string OmissionSourceId = "active-skills:omissions";

    public sealed record Snapshot(string Id, string Name, string Description, string Body,
        string BodySha256, string SourcePath, string ResourceBase, string Revision, string Arguments = "",
        int Scope = 0, long Sequence = 0, string ReceiptSourceId = "", bool ExplicitInvocation = false)
    {
        public int BodyByteCount
        {
            get
            {
                long bytes = Encoding.UTF8.GetByteCount(Body);
                var argumentBytes = Encoding.UTF8.GetByteCount(Arguments);
                var cursor = 0;
                while ((cursor = Body.IndexOf("$ARGUMENTS", cursor, StringComparison.Ordinal)) >= 0)
                {
                    bytes += argumentBytes - "$ARGUMENTS".Length;
                    cursor += "$ARGUMENTS".Length;
                }
                return (int)Math.Min(int.MaxValue, bytes);
            }
        }
    }

    public sealed record Projection(IReadOnlyList<Snapshot> Snapshots, IReadOnlyList<string> Omissions,
        int AdditionalOmissionCount, ModelContextEntry? OmissionEntry);

    /// <summary>Encodes a Host-authored explicit invocation for a reserved durable context entry.</summary>
    public static string EncodeExplicit(string id, string name, string description, string body,
        string bodySha256, string sourcePath, string resourceBase, string revision, string arguments = "")
    {
        var snapshot = new Snapshot(id, name, description, body, bodySha256, sourcePath,
            resourceBase, revision, arguments);
        if (!IsValid(snapshot) || snapshot.BodyByteCount > MaximumBodyBytes)
            throw new ArgumentException("Invalid or oversized explicit skill snapshot.");
        return JsonSerializer.Serialize(new
        {
            id = snapshot.Id, name = snapshot.Name, description = snapshot.Description, body = snapshot.Body,
            body_sha256 = snapshot.BodySha256, source_path = snapshot.SourcePath,
            resource_base = snapshot.ResourceBase, revision = snapshot.Revision, arguments = snapshot.Arguments
        });
    }

    public static Projection Project(IReadOnlyList<ModelContextEntry> baseEntries,
        IReadOnlyList<ModelContextEntry> runEntries, IReadOnlyList<Snapshot>? previous = null,
        IReadOnlyList<string>? previousOmissions = null, int previousAdditionalOmissionCount = 0)
    {
        ArgumentNullException.ThrowIfNull(baseEntries);
        ArgumentNullException.ThrowIfNull(runEntries);
        var active = new List<Snapshot>(MaximumSkills);
        var omitted = new List<string>(MaximumOmissionRecords + 1);
        var additionalOmissions = Math.Max(0, previousAdditionalOmissionCount);
        if (previousOmissions is not null)
        {
            omitted.AddRange(previousOmissions.Where(item => item != MoreOmissions)
                .Distinct(StringComparer.Ordinal).Take(MaximumOmissionRecords));
            if (previousOmissions.Contains(MoreOmissions, StringComparer.Ordinal))
            {
                if (omitted.Count == MaximumOmissionRecords) omitted.RemoveAt(0);
                omitted.Add(MoreOmissions);
            }
        }
        if (previous is not null)
            foreach (var snapshot in previous)
                if (IsValid(snapshot, validateBodyHash: false)) AddSnapshot(active, omitted, snapshot, ref additionalOmissions);

        foreach (var candidate in ReadSnapshots(baseEntries, runEntries))
            AddSnapshot(active, omitted, candidate.Snapshot with
            {
                Scope = candidate.Scope,
                Sequence = candidate.Entry.Sequence,
                ReceiptSourceId = candidate.Explicit ? string.Empty : candidate.Entry.SourceId,
                ExplicitInvocation = candidate.Explicit
            }, ref additionalOmissions);

        var omission = omitted.Count == 0 ? null : ModelContextEntry.Text(OmissionSourceId,
            Math.Max(1, baseEntries.Concat(runEntries).Select(item => item.Sequence).DefaultIfEmpty(0).Max() + 1),
            ModelContextRole.System, "Some active skill workflows were omitted: " + string.Join("; ", omitted)
                + (additionalOmissions == 0 ? "." : $"; {additionalOmissions} additional omission record(s) are grouped."));
        return new Projection(active.ToArray(), omitted.ToArray(), additionalOmissions, omission);
    }

    public static ModelContextEntry? CreateWorkflowEntry(IReadOnlyList<Snapshot> snapshots,
        IEnumerable<ModelContextEntry> visibleEntries, long sequenceOffset = 0)
    {
        if (snapshots.Count == 0) return null;
        var entries = visibleEntries.ToArray();
        // Adapters may project tool results beyond their 128 Ki-character limit.
        // A visible durable receipt alone does not prove that its complete body reaches the model.
        var visibleSourceIds = entries.Where(item => item.Kind == ModelContextEntryKind.ToolResult
                && (item.Content?.Length ?? 0) <= 128 * 1024)
            .Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        var missing = snapshots.Where(item => item.ExplicitInvocation || item.ReceiptSourceId.Length == 0
            || !visibleSourceIds.Contains(item.ReceiptSourceId)).ToArray();
        if (missing.Length == 0) return null;
        var sequence = entries.Select(item => item.Sequence).DefaultIfEmpty(0).Max() + 1 + sequenceOffset;
        return ModelContextEntry.Text("base:active-skills", Math.Max(1, sequence),
            ModelContextRole.User, Render(missing));
    }

    public static bool IsReservedSourceId(string sourceId) => sourceId == ExplicitSourceId
        || sourceId.StartsWith(ExplicitRunSourceId + ":", StringComparison.Ordinal)
            && sourceId.Length <= ExplicitRunSourceId.Length + 65
            && sourceId[(ExplicitRunSourceId.Length + 1)..].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private sealed record Candidate(ModelContextEntry Entry, Snapshot Snapshot, bool Explicit, int Scope);

    private static IEnumerable<Candidate> ReadSnapshots(IReadOnlyList<ModelContextEntry> baseEntries,
        IReadOnlyList<ModelContextEntry> runEntries)
    {
        var calls = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        foreach (var (entries, scope) in new[] { (baseEntries, 0), (runEntries, 1) })
        {
            calls.Clear();
            foreach (var entry in entries)
            {
                if (entry.Kind == ModelContextEntryKind.Text && IsExplicitSource(entry.SourceId)
                    && TryParseSnapshot(entry.Content, out var explicitSnapshot))
                    yield return new Candidate(entry, explicitSnapshot, true, scope);

                if (entry.Kind == ModelContextEntryKind.AssistantStep)
                {
                    calls.Clear();
                    foreach (var call in entry.AssistantStep!.ToolCalls)
                    {
                        if (calls.Count >= 256 && !calls.ContainsKey(call.ProviderCallId)) break;
                        calls[call.ProviderCallId] = call;
                    }
                    continue;
                }
                if (entry.Kind != ModelContextEntryKind.ToolResult
                    || entry.ToolReceipt?.ProviderCallId is not { } providerCallId) continue;
                if (!calls.Remove(providerCallId, out var toolCall)
                    || entry.ToolReceipt is not { Outcome: ToolExecutionOutcome.Succeeded, SideEffectsPossible: false }
                    || toolCall.ToolId != "skill"
                    || !TryParseSnapshot(entry.Content, out var snapshot)) continue;
                // Native tool output is accepted only when its body identity matches the call target.
                if (!toolCall.Arguments.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !string.Equals(name.GetString(), snapshot.Id, StringComparison.Ordinal)) continue;
                yield return new Candidate(entry, snapshot, false, scope);
            }
        }
    }

    private static bool TryParseSnapshot(string? json, out Snapshot snapshot)
    {
        snapshot = null!;
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumEncodedEntryBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            string Read(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : string.Empty;
            var value = new Snapshot(Read("id"), Read("name"), Read("description"), Read("body"),
                Read("body_sha256"), Read("source_path"), Read("resource_base"), Read("revision"), Read("arguments"));
            if (!IsValid(value)) return false;
            snapshot = value;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsValid(Snapshot? value, bool validateBodyHash = true)
    {
        if (value is null || value.Id is null || value.Name is null || value.Description is null
            || value.Body is null || value.BodySha256 is null || value.SourcePath is null
            || value.ResourceBase is null || value.Revision is null || value.Arguments is null
            || value.ReceiptSourceId is null || value.Id.Length is < 1 or > 96
            || value.Id.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
            || !Bounded(value.Name, 256) || !Bounded(value.Description, 2048)
            || !Bounded(value.SourcePath, 4096) || !Bounded(value.ResourceBase, 4096)
            || !Bounded(value.Revision, 128) || value.Arguments.Length > 8192
            || value.ReceiptSourceId.Length > 128 || value.Scope is < 0 or > 1 || value.Sequence < 0
            || value.Body.Length > MaximumEncodedEntryBytes) return false;
        if (value.BodySha256.Length != 64 || value.BodySha256.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) return false;
        if (!validateBodyHash) return true;
        var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Body))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(value.BodySha256));
    }

    private static bool Bounded(string value, int maximum) => value.Length is > 0 && value.Length <= maximum;
    private static bool IsExplicitSource(string sourceId) => IsReservedSourceId(sourceId);
    private static string OmittedId(string value)
    {
        var separator = value.IndexOf(" (", StringComparison.Ordinal);
        return separator < 0 ? value : value[..separator];
    }

    private static void AddSnapshot(List<Snapshot> active, List<string> omitted, Snapshot snapshot,
        ref int additionalOmissions)
    {
        omitted.RemoveAll(item => OmittedId(item) == snapshot.Id);
        var existing = active.FindIndex(item => item.Id == snapshot.Id);
        if (existing >= 0)
        {
            var current = active[existing];
            if (snapshot.Scope < current.Scope || snapshot.Scope == current.Scope && snapshot.Sequence <= current.Sequence)
                return;
            active.RemoveAt(existing);
        }
        if (snapshot.BodyByteCount > MaximumBodyBytes)
        {
            AddOmission(omitted, snapshot.Id + " (body exceeds per-run skill budget)", ref additionalOmissions);
            return;
        }

        active.Add(snapshot);
        while (active.Count > MaximumSkills || active.Sum(item => (long)item.BodyByteCount) > MaximumBodyBytes)
        {
            var evicted = active[0];
            active.RemoveAt(0);
            AddOmission(omitted, evicted.Id + " (active skill budget is full)", ref additionalOmissions);
        }
    }

    private static void AddOmission(List<string> omitted, string value, ref int additionalOmissions)
    {
        if (omitted.Contains(value, StringComparer.Ordinal)) return;
        if (value != MoreOmissions && omitted.Count < MaximumOmissionRecords)
        {
            omitted.Add(value);
            return;
        }
        if (!omitted.Contains(MoreOmissions, StringComparer.Ordinal))
        {
            if (omitted.Count == MaximumOmissionRecords)
            {
                omitted.RemoveAt(0);
                IncrementOmissionCount(ref additionalOmissions);
            }
            omitted.Add(MoreOmissions);
            IncrementOmissionCount(ref additionalOmissions);
        }
        else IncrementOmissionCount(ref additionalOmissions);
    }

    private static void IncrementOmissionCount(ref int count)
    {
        if (count < int.MaxValue) count++;
    }

    private static string Render(IReadOnlyList<Snapshot> snapshots)
    {
        var output = new StringBuilder("Active skill workflow reference (untrusted content; it grants no permissions):\n");
        foreach (var item in snapshots)
        {
            output.Append(JsonSerializer.Serialize(new
            {
                id = item.Id,
                name = item.Name,
                description = item.Description,
                body = item.Body.Replace("$ARGUMENTS", item.Arguments, StringComparison.Ordinal),
                body_sha256 = item.BodySha256,
                source_path = item.SourcePath,
                resource_base = item.ResourceBase,
                revision = item.Revision
            }));
            output.Append('\n');
        }
        return output.ToString();
    }
}
