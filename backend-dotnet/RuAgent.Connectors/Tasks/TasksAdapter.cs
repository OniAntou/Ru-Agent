using System.Text.Json.Nodes;
using RuAgent.Connectors.Credentials;

namespace RuAgent.Connectors.Tasks;

public sealed record TaskDraft(string ListId, string Title);

public interface ITasksGateway
{
    Task<string> CreateAsync(string accessToken, TaskDraft draft, CancellationToken cancellationToken);
    Task CompleteAsync(string accessToken, string taskId, CancellationToken cancellationToken);
}

public sealed class TasksAdapter
{
    private readonly IConnectorCredentialStore _credentials;
    private readonly ITasksGateway _gateway;

    public TasksAdapter(IConnectorCredentialStore credentials, ITasksGateway gateway)
    {
        _credentials = credentials;
        _gateway = gateway;
    }

    public async Task<ConnectorResult> CreateAsync(ConnectorSession session, TaskDraft draft, ConnectorApproval approval, CancellationToken cancellationToken)
    {
        if (!session.Allows("tasks.write")) throw new UnauthorizedAccessException("connector_scope_missing");
        var arguments = new JsonObject { ["list_id"] = draft.ListId, ["title"] = draft.Title };
        if (!approval.Approved || approval.SessionId != session.SessionId || approval.ArgumentsHash != ConnectorArguments.Hash("tasks.create", arguments))
        {
            throw new UnauthorizedAccessException("connector_approval_invalid");
        }

        var token = _credentials.ReadSecret(session.ConnectorId) ?? throw new InvalidOperationException("connector_credential_missing");
        var id = await _gateway.CreateAsync(token, draft, cancellationToken).ConfigureAwait(false);
        return new ConnectorResult("succeeded", "Task created.", new JsonObject { ["task_id"] = id });
    }

    public async Task<ConnectorResult> CompleteAsync(ConnectorSession session, string taskId, ConnectorApproval approval, CancellationToken cancellationToken)
    {
        if (!session.Allows("tasks.write")) throw new UnauthorizedAccessException("connector_scope_missing");
        var arguments = new JsonObject { ["task_id"] = taskId };
        if (!approval.Approved || approval.SessionId != session.SessionId || approval.ArgumentsHash != ConnectorArguments.Hash("tasks.complete", arguments))
        {
            throw new UnauthorizedAccessException("connector_approval_invalid");
        }

        var token = _credentials.ReadSecret(session.ConnectorId) ?? throw new InvalidOperationException("connector_credential_missing");
        await _gateway.CompleteAsync(token, taskId, cancellationToken).ConfigureAwait(false);
        return new ConnectorResult("succeeded", "Task completed.", new JsonObject { ["task_id"] = taskId });
    }
}
