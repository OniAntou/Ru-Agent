using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools;

public sealed record ToolExecutionContext(
    string RequestId,
    string SessionId,
    string? CorrelationId,
    string CapabilityClass,
    int DeadlineMs,
    bool Approved,
    string? ApprovalToken = null,
    bool ConfinementRequired = true,
    IReadOnlySet<string>? SessionScopes = null,
    string RunId = "",
    bool IsAuthenticatedV2Execution = false,
    bool ImageInputAccepted = false)
{
    internal Terminal.CanonicalCommandIntent? AuthorizedTerminalIntent { get; init; }
}

public sealed record ToolExecutionOutcome(
    string Status,
    JsonObject Output,
    string? ErrorCode,
    string SafeSummary,
    string VerificationState,
    string? EndpointRole = null,
    string? EndpointHash = null,
    int DurationMs = 0,
    ToolOutputImage? Image = null)
{
    public static ToolExecutionOutcome Succeeded(
        JsonObject output,
        string verificationState,
        string safeSummary,
        string? endpointRole = null,
        string? endpointHash = null) =>
        new(
            ToolResultStatuses.Succeeded,
            output,
            null,
            safeSummary,
            verificationState,
            endpointRole,
            endpointHash);

    public static ToolExecutionOutcome Failed(
        string errorCode,
        string safeSummary,
        string verificationState = ToolVerificationStates.Failed,
        string? endpointRole = null,
        string? endpointHash = null) =>
        new(
            ToolResultStatuses.Failed,
            new JsonObject(),
            errorCode,
            safeSummary,
            verificationState,
            endpointRole,
            endpointHash);
}

public delegate ValueTask<ToolExecutionOutcome> ToolExecutor(
    ToolExecutionContext context,
    JsonObject arguments,
    CancellationToken cancellationToken);
