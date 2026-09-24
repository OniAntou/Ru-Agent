using System.Text.Json;
using RuAgent.Harness.Core.Policy;

namespace RuAgent.Harness.Core;

public enum ToolAccessKind
{
    ReadOnly,
    Mutation,
    Terminal,
}

public sealed record ToolProposal
{
    public ToolProposal(
        RunId runId,
        ModelStepId modelStepId,
        ToolCallId toolCallId,
        string providerCallId,
        int modelOrder,
        string toolId,
        string toolVersion,
        JsonElement arguments)
    {
        _ = runId.Value;
        _ = modelStepId.Value;
        _ = toolCallId.Value;
        if (!ProviderCallIdContract.IsValid(providerCallId))
        {
            throw new ArgumentException("Provider call ID must be a bounded safe token.", nameof(providerCallId));
        }

        if (modelOrder <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modelOrder));
        }

        if (string.IsNullOrWhiteSpace(toolId) || toolId.Length > 128)
        {
            throw new ArgumentException("Tool ID must be bounded.", nameof(toolId));
        }

        if (string.IsNullOrWhiteSpace(toolVersion) || toolVersion.Length > 32)
        {
            throw new ArgumentException("Tool version must be bounded.", nameof(toolVersion));
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Tool arguments must be an object.", nameof(arguments));
        }

        RunId = runId;
        ModelStepId = modelStepId;
        ToolCallId = toolCallId;
        ProviderCallId = providerCallId;
        ModelOrder = modelOrder;
        ToolId = toolId;
        ToolVersion = toolVersion;
        Arguments = arguments.Clone();
    }

    public RunId RunId { get; }
    public ModelStepId ModelStepId { get; }
    public ToolCallId ToolCallId { get; }
    public string ProviderCallId { get; }
    public int ModelOrder { get; }
    public string ToolId { get; }
    public string ToolVersion { get; }
    public JsonElement Arguments { get; }
}

public sealed record ToolProposalDecision
{
    private ToolProposalDecision(
        PolicyDecision decision,
        ToolAccessKind accessKind,
        string? canonicalIntentToken,
        ApprovalId? approvalId,
        string safeCode,
        string? denyHint,
        long? gatewayGeneration)
    {
        if (gatewayGeneration is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gatewayGeneration));
        }

        Decision = decision;
        AccessKind = accessKind;
        CanonicalIntentToken = canonicalIntentToken;
        ApprovalId = approvalId;
        SafeCode = safeCode;
        DenyHint = denyHint;
        GatewayGeneration = gatewayGeneration;
    }

    /// <summary>
    /// Bounded repair hint that travels to the model next to a deny code; it
    /// names the argument shape the call was missing and never carries file
    /// contents or paths.
    /// </summary>
    public const int MaximumDenyHintLength = 200;

    public PolicyDecision Decision { get; }
    public ToolAccessKind AccessKind { get; }
    public string? CanonicalIntentToken { get; }
    public ApprovalId? ApprovalId { get; }
    public string SafeCode { get; }
    public string? DenyHint { get; }
    public long? GatewayGeneration { get; }

    public static ToolProposalDecision Allow(
        ToolAccessKind accessKind,
        string canonicalIntentToken,
        long? gatewayGeneration = null)
    {
        ValidateAccessKind(accessKind);

        if (string.IsNullOrWhiteSpace(canonicalIntentToken)
            || canonicalIntentToken.Length > 4096)
        {
            throw new ArgumentException("Canonical intent token must be bounded.", nameof(canonicalIntentToken));
        }

        return new ToolProposalDecision(
            PolicyDecision.Allow,
            accessKind,
            canonicalIntentToken,
            null,
            "allowed",
            denyHint: null,
            gatewayGeneration);
    }

    public static ToolProposalDecision Ask(
        ToolAccessKind accessKind,
        ApprovalId approvalId,
        string safeCode = "approval_required",
        long? gatewayGeneration = null)
    {
        ValidateAccessKind(accessKind);
        _ = approvalId.Value;
        if (!IsSafeCode(safeCode))
        {
            throw new ArgumentException("Ask code must be bounded.", nameof(safeCode));
        }

        return new ToolProposalDecision(
            PolicyDecision.Ask,
            accessKind,
            null,
            approvalId,
            safeCode,
            denyHint: null,
            gatewayGeneration);
    }

    public static ToolProposalDecision Deny(
        ToolAccessKind accessKind,
        string safeCode,
        string? denyHint = null,
        long? gatewayGeneration = null)
    {
        ValidateAccessKind(accessKind);
        if (!IsSafeCode(safeCode))
        {
            throw new ArgumentException("Deny code must be bounded.", nameof(safeCode));
        }

        return new ToolProposalDecision(
            PolicyDecision.Deny,
            accessKind,
            null,
            null,
            safeCode,
            BoundHint(denyHint),
            gatewayGeneration);
    }

    private static string? BoundHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        var cleaned = hint.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (cleaned.Contains('\0'))
        {
            return null;
        }

        return cleaned.Length <= MaximumDenyHintLength
            ? cleaned
            : cleaned[..MaximumDenyHintLength];
    }

    private static void ValidateAccessKind(ToolAccessKind accessKind)
    {
        if (!Enum.IsDefined(accessKind))
        {
            throw new ArgumentOutOfRangeException(nameof(accessKind));
        }
    }

    private static bool IsSafeCode(string? value) =>
        value is { Length: >= 1 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '_'
                or '.');
}

public sealed record ApprovedToolExecution
{
    public ApprovedToolExecution(
        ToolProposal proposal,
        ExecutionId executionId,
        string canonicalIntentToken,
        long? gatewayGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        _ = executionId.Value;
        if (string.IsNullOrWhiteSpace(canonicalIntentToken)
            || canonicalIntentToken.Length > 4096)
        {
            throw new ArgumentException("Canonical intent token must be bounded.", nameof(canonicalIntentToken));
        }

        if (gatewayGeneration is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gatewayGeneration));
        }

        Proposal = proposal;
        ExecutionId = executionId;
        CanonicalIntentToken = canonicalIntentToken;
        GatewayGeneration = gatewayGeneration;
    }

    public ToolProposal Proposal { get; }
    public ExecutionId ExecutionId { get; }
    public string CanonicalIntentToken { get; }
    public long? GatewayGeneration { get; }
}
