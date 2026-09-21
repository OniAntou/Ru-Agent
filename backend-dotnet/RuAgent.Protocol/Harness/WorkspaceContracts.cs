using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Harness;

public sealed record WorkspaceTrustApiState(
    string WorkspaceId,
    string TrustRequestId,
    string Decision,
    bool IsTrusted,
    string UpdatedAtUtc);

public static class WorkspaceConcurrencyMode
{
    public const string Exclusive = "exclusive";
    public const string Shared = "shared";
}

public static class WorkspaceWriterKind
{
    public const string RuPeer = "ru_peer";
    public const string UnknownExternal = "unknown_external";
}

public sealed record WorkspaceConcurrencyPolicy(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("max_concurrent_runs")] int MaxConcurrentRuns = 1,
    [property: JsonPropertyName("lease_ttl_seconds")] int LeaseTtlSeconds = 30,
    [property: JsonPropertyName("merge_text_conflicts")] bool MergeTextConflicts = true,
    [property: JsonPropertyName("external_writer_detection")] bool ExternalWriterDetection = true)
{
    public void Validate()
    {
        if (Mode is not (WorkspaceConcurrencyMode.Exclusive or WorkspaceConcurrencyMode.Shared))
        {
            throw new AgentProtocolException("invalid_workspace_concurrency_mode");
        }

        if (MaxConcurrentRuns is < 1 or > 32 || LeaseTtlSeconds is < 5 or > 600)
        {
            throw new AgentProtocolException("invalid_workspace_concurrency_policy");
        }
    }
}

public sealed record WorkspaceWriteSet(
    [property: JsonPropertyName("read_only")] bool ReadOnly,
    [property: JsonPropertyName("paths")] IReadOnlyList<string> Paths)
{
    public static void Validate(bool readOnly, IReadOnlyList<string>? paths)
    {
        if (paths is null)
        {
            throw new AgentProtocolException("invalid_terminal_write_set");
        }

        if (readOnly && paths.Count > 0)
        {
            throw new AgentProtocolException("invalid_terminal_write_set");
        }

        if (paths.Count > 256
            || paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 4096)
            || paths.Any(path => Path.IsPathRooted(path)
                || path.Replace('\\', '/').Split('/').Any(segment => segment == "..")))
        {
            throw new AgentProtocolException("invalid_terminal_write_set");
        }
    }
}

public sealed record WorkspaceActivityItem(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("writer_kind")] string? WriterKind,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("created_at_utc")] string CreatedAtUtc,
    [property: JsonPropertyName("details")] string? Details);

public sealed record WorkspaceChangeItem(
    [property: JsonPropertyName("change_id")] string ChangeId,
    [property: JsonPropertyName("workspace_id")] string WorkspaceId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("before_sha256")] string? BeforeSha256,
    [property: JsonPropertyName("after_sha256")] string? AfterSha256,
    [property: JsonPropertyName("created_at_utc")] string CreatedAtUtc);

public sealed record WorkspaceChangeRestoreRequest(
    [property: JsonPropertyName("expected_current_sha256")] string ExpectedCurrentSha256);

public sealed record WorkspaceChangeRestoreResponse(
    [property: JsonPropertyName("change_id")] string ChangeId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("restored_sha256")] string RestoredSha256,
    [property: JsonPropertyName("restore_change_id")] string RestoreChangeId);

public static class WorkspaceContracts
{
    public const string TrustRequestIdField = "trust_request_id";
    public const string DecisionField = "decision";
    public const string IsTrustedField = "is_trusted";
    public const string UpdatedAtField = "updated_at_utc";

    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex WorkspaceIdPattern = new(@"^ws_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex TrustRequestIdPattern = new(@"^trust_[0-9a-f]{32}$", RegexOptions.Compiled);

    public static void ValidateWorkspaceId(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || !WorkspaceIdPattern.IsMatch(workspaceId))
        {
            throw new AgentProtocolException("invalid_workspace_id");
        }
    }

    public static void ValidateTrustRequestId(string trustRequestId)
    {
        if (string.IsNullOrWhiteSpace(trustRequestId) || !TrustRequestIdPattern.IsMatch(trustRequestId))
        {
            throw new AgentProtocolException("invalid_trust_request_id");
        }
    }

    public static void ValidateDecision(string decision)
    {
        if (decision is not ("approve" or "deny"))
        {
            throw new AgentProtocolException("invalid_trust_decision");
        }
    }

    public static void ValidateDisplayHash(string displayHash)
    {
        if (string.IsNullOrWhiteSpace(displayHash) || !Sha256Pattern.IsMatch(displayHash))
        {
            throw new AgentProtocolException("invalid_display_hash");
        }
    }

    public static void Validate(WorkspaceTrustApiState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateWorkspaceId(state.WorkspaceId);
        ValidateTrustRequestId(state.TrustRequestId);
        ValidateDecision(state.Decision);
        if (!DateTimeOffset.TryParse(state.UpdatedAtUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _))
        {
            throw new AgentProtocolException("invalid_updated_at");
        }
    }

    public static bool IsTrustedExactMatch(
        string expectedWorkspaceId,
        string expectedVolumeSerial,
        string expectedRootFileId,
        string expectedDisplayHash,
        string actualWorkspaceId,
        string actualVolumeSerial,
        string actualRootFileId,
        string actualDisplayHash) =>
        string.Equals(expectedWorkspaceId, actualWorkspaceId, StringComparison.Ordinal)
        && string.Equals(expectedVolumeSerial, actualVolumeSerial, StringComparison.Ordinal)
        && string.Equals(expectedRootFileId, actualRootFileId, StringComparison.Ordinal)
        && string.Equals(expectedDisplayHash, actualDisplayHash, StringComparison.Ordinal);
}
