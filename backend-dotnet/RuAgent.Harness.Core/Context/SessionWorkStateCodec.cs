using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuAgent.Harness.Core.Context;

public static class SessionWorkStateCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private const int FormatVersion = 1;

    public static byte[] Serialize(SessionWorkState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var document = new WorkStateDocument(
            FormatVersion,
            state.SessionId.Value, state.WorkspaceId.Value, state.WorkspaceIdentitySha256,
            state.SourceRunId.Value, state.Version, state.SourceEventId, state.UserGoal,
            state.UserConstraints, state.EarlierUserIntent, state.EarlierUserIntentOmitted,
            state.HasUnrepresentedReceipts,
            state.Tasks, state.ObservedFiles, state.Verifications,
            state.Blocker, state.NextAction);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > SessionWorkState.MaximumUtf8Bytes)
            throw new ArgumentException("Work state exceeds its serialized cap.", nameof(state));
        return bytes;
    }

    public static SessionWorkState Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > SessionWorkState.MaximumUtf8Bytes)
            throw new InvalidDataException("Work state envelope exceeds its cap.");
        _ = StrictUtf8.GetString(bytes);
        try
        {
            using var parsed = JsonDocument.Parse(bytes.ToArray());
            ValidateUniqueProperties(parsed.RootElement);
            ValidateFields(parsed.RootElement, RootFields);
            var document = JsonSerializer.Deserialize<WorkStateDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Work state is empty.");
            if (document.FormatVersion != FormatVersion)
                throw new InvalidDataException("Work state format version is unsupported.");
            return new SessionWorkState(
                new SessionId(document.SessionId), new WorkspaceId(document.WorkspaceId),
                document.WorkspaceIdentitySha256, new RunId(document.SourceRunId), document.Version,
                document.SourceEventId, document.UserGoal, document.UserConstraints,
                document.EarlierUserIntent, document.EarlierUserIntentOmitted,
                document.HasUnrepresentedReceipts, document.Tasks,
                document.ObservedFiles,
                document.Verifications, document.Blocker, document.NextAction);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            throw new InvalidDataException("Work state document is malformed.", error);
        }
    }

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Work state has duplicate fields.");
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateUniqueProperties(item);
    }

    private static readonly string[] RootFields =
    [
        "formatVersion", "sessionId", "workspaceId", "workspaceIdentitySha256", "sourceRunId", "version",
        "sourceEventId", "userGoal", "userConstraints", "earlierUserIntent", "earlierUserIntentOmitted", "hasUnrepresentedReceipts",
        "tasks", "observedFiles", "verifications", "blocker", "nextAction",
    ];

    private static void ValidateFields(JsonElement root, IReadOnlyCollection<string> required)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Work state must be an object.");
        var actual = root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (actual.Count != required.Count || required.Any(field => !actual.Contains(field)))
            throw new InvalidDataException("Work state fields are missing or unsupported.");
        ValidateNested(root);
    }

    private static void ValidateNested(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var fields = element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            if (fields.ContainsKey("status") && fields.ContainsKey("sourceRunId"))
                RequireExactFields(fields, "text", "status", "source", "sourceRunId", "sourceEventId");
            else if (fields.ContainsKey("text") && fields.ContainsKey("sourceRunId"))
                RequireExactFields(fields, "text", "sourceRunId", "sourceEventId");
            else if (fields.ContainsKey("sha256"))
                RequireExactFields(fields, "path", "sha256", "sourceRunId", "sourceEventId", "observedAt");
            else if (fields.ContainsKey("outcomeCode"))
                RequireExactFields(fields, "command", "outcomeCode", "succeeded", "sourceRunId", "sourceEventId", "recordedAt");
            foreach (var child in fields.Values) ValidateNested(child);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateNested(child);
    }

    private static void RequireExactFields(IReadOnlyDictionary<string, JsonElement> fields, params string[] expected)
    {
        if (fields.Count != expected.Length || expected.Any(field => !fields.ContainsKey(field)))
            throw new InvalidDataException("Work state record fields are missing or unsupported.");
    }

    private sealed record WorkStateDocument(
        int FormatVersion, string SessionId, string WorkspaceId, string WorkspaceIdentitySha256,
        string SourceRunId, long Version, string SourceEventId, SessionWorkFact UserGoal,
        IReadOnlyList<SessionWorkFact> UserConstraints, IReadOnlyList<SessionWorkFact> EarlierUserIntent,
        bool EarlierUserIntentOmitted, bool HasUnrepresentedReceipts,
        IReadOnlyList<SessionWorkItem> Tasks,
        IReadOnlyList<SessionObservedFile> ObservedFiles,
        IReadOnlyList<SessionVerificationRecord> Verifications,
        SessionWorkFact? Blocker, SessionWorkFact? NextAction);
}
