using System.Text.Json.Nodes;
using RuAgent.Connectors.Credentials;

namespace RuAgent.Connectors.Mail;

public sealed record MailDraft(string To, string Subject, string Body);

public interface IMailGateway
{
    Task<string> SendAsync(string accessToken, MailDraft draft, CancellationToken cancellationToken);
}

public sealed class MailAdapter
{
    private readonly IConnectorCredentialStore _credentials;
    private readonly IMailGateway _gateway;

    public MailAdapter(IConnectorCredentialStore credentials, IMailGateway gateway)
    {
        _credentials = credentials;
        _gateway = gateway;
    }

    public async Task<ConnectorResult> SendAsync(ConnectorSession session, MailDraft draft, ConnectorApproval approval, CancellationToken cancellationToken)
    {
        if (!session.Allows("mail.send")) throw new UnauthorizedAccessException("connector_scope_missing");
        var arguments = new JsonObject { ["to"] = draft.To, ["subject"] = draft.Subject, ["body"] = draft.Body };
        if (!approval.Approved || approval.SessionId != session.SessionId || approval.ArgumentsHash != ConnectorArguments.Hash("mail.send", arguments))
        {
            throw new UnauthorizedAccessException("connector_approval_invalid");
        }

        var token = _credentials.ReadSecret(session.ConnectorId) ?? throw new InvalidOperationException("connector_credential_missing");
        var id = await _gateway.SendAsync(token, draft, cancellationToken).ConfigureAwait(false);
        return new ConnectorResult("succeeded", "Mail sent.", new JsonObject { ["message_id"] = id });
    }
}
