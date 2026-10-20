namespace RuApp.Services;

internal enum AgentApprovalOperation
{
    Deny,
    AllowOnce,
    AllowSessionDirectory,
    AllowAlways,
}

/// <summary>Owns approval request identity and the command/refresh lifecycle.</summary>
internal sealed class AgentRunApprovalCoordinator
{
    internal string? Id { get; private set; }
    internal string? Summary { get; private set; }
    internal string? Command { get; private set; }
    internal bool HasApproval => Id is not null;

    internal void SetId(string? id)
    {
        Id = id;
        if (id is null) Command = null;
    }

    internal void SetSummary(string? summary) => Summary = summary;
    internal void SetCommand(string? command) => Command = command;
    internal void Set(string? id, string? summary)
    {
        SetId(id);
        SetSummary(summary);
    }

    internal void Clear() => Set(null, null);

    internal async Task SubmitAsync(
        string? runId,
        string? approvalId,
        AgentApprovalOperation operation,
        IAgentRunClient client,
        Action<string> setStatus,
        Func<string, string, bool> ownsRequest,
        Func<CancellationToken, Task> refresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(setStatus);
        ArgumentNullException.ThrowIfNull(ownsRequest);
        ArgumentNullException.ThrowIfNull(refresh);
        if (runId is null || approvalId is null)
        {
            throw new InvalidOperationException("No Agent approval is pending.");
        }

        if (operation == AgentApprovalOperation.Deny)
        {
            setStatus("Denying approval...");
            await client.DenyAsync(runId, approvalId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            setStatus("Submitting approval...");
            // Session permission is decided server-side. UI never selects a scope
            // beyond the explicitly offered approval choice.
            string scope = operation switch
            {
                AgentApprovalOperation.AllowSessionDirectory => "session",
                AgentApprovalOperation.AllowAlways => "always",
                _ => "once",
            };
            await client.ApproveAsync(runId, approvalId, scope, cancellationToken).ConfigureAwait(false);
        }

        if (ownsRequest(runId, approvalId))
        {
            await refresh(cancellationToken).ConfigureAwait(false);
        }
    }
}
