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
        int modelOrder,
        string toolId,
        string toolVersion,
        JsonElement arguments)
    {
        _ = runId.Value;
        _ = modelStepId.Value;
        _ = toolCallId.Value;
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
        ModelOrder = modelOrder;
        ToolId = toolId;
        ToolVersion = toolVersion;
        Arguments = arguments.Clone();
    }

    public RunId RunId { get; }
    public ModelStepId ModelStepId { get; }
    public ToolCallId ToolCallId { get; }
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
        string safeCode)
    {
        Decision = decision;
        AccessKind = accessKind;
        CanonicalIntentToken = canonicalIntentToken;
        ApprovalId = approvalId;
        SafeCode = safeCode;
    }

    public PolicyDecision Decision { get; }
    public ToolAccessKind AccessKind { get; }
    public string? CanonicalIntentToken { get; }
    public ApprovalId? ApprovalId { get; }
    public string SafeCode { get; }

    public static ToolProposalDecision Allow(
        ToolAccessKind accessKind,
        string canonicalIntentToken)
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
            "allowed");
    }

    public static ToolProposalDecision Ask(
        ToolAccessKind accessKind,
        ApprovalId approvalId)
    {
        ValidateAccessKind(accessKind);
        _ = approvalId.Value;
        return new ToolProposalDecision(
            PolicyDecision.Ask,
            accessKind,
            null,
            approvalId,
            "approval_required");
    }

    public static ToolProposalDecision Deny(ToolAccessKind accessKind, string safeCode)
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
            safeCode);
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
        string canonicalIntentToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        _ = executionId.Value;
        if (string.IsNullOrWhiteSpace(canonicalIntentToken)
            || canonicalIntentToken.Length > 4096)
        {
            throw new ArgumentException("Canonical intent token must be bounded.", nameof(canonicalIntentToken));
        }

        Proposal = proposal;
        ExecutionId = executionId;
        CanonicalIntentToken = canonicalIntentToken;
    }

    public ToolProposal Proposal { get; }
    public ExecutionId ExecutionId { get; }
    public string CanonicalIntentToken { get; }
}
