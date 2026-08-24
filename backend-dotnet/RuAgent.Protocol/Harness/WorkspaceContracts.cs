using System.Text.RegularExpressions;

namespace RuAgent.Protocol.Harness;

public sealed record WorkspaceTrustApiState(
    string WorkspaceId,
    string TrustRequestId,
    string Decision,
    bool IsTrusted,
    string UpdatedAtUtc);

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
