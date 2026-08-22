using System.Text.Json.Nodes;
using RuAgent.Connectors.Credentials;

namespace RuAgent.Connectors.Calendar;

public sealed record CalendarEventDraft(string CalendarId, string Title, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public interface ICalendarGateway
{
    Task<string> CreateAsync(string accessToken, CalendarEventDraft draft, CancellationToken cancellationToken);
}

public sealed class CalendarAdapter
{
    private readonly IConnectorCredentialStore _credentials;
    private readonly ICalendarGateway _gateway;

    public CalendarAdapter(IConnectorCredentialStore credentials, ICalendarGateway gateway)
    {
        _credentials = credentials;
        _gateway = gateway;
    }

    public ConnectorResult PreviewCreate(ConnectorSession session, CalendarEventDraft draft)
    {
        Ensure(session, "calendar.write");
        var args = ToJson(draft);
        return new ConnectorResult(
            "preview",
            "Calendar event preview ready.",
            new JsonObject
            {
                ["arguments_hash"] = ConnectorArguments.Hash("calendar.create", args),
                ["title"] = draft.Title,
                ["calendar_id"] = draft.CalendarId,
            });
    }

    public async Task<ConnectorResult> CreateAsync(
        ConnectorSession session,
        CalendarEventDraft draft,
        ConnectorApproval approval,
        CancellationToken cancellationToken)
    {
        Ensure(session, "calendar.write");
        var arguments = ToJson(draft);
        EnsureApproval(session, approval, ConnectorArguments.Hash("calendar.create", arguments));
        var token = _credentials.ReadSecret(session.ConnectorId)
            ?? throw new InvalidOperationException("connector_credential_missing");
        var id = await _gateway.CreateAsync(token, draft, cancellationToken).ConfigureAwait(false);
        return new ConnectorResult("succeeded", "Calendar event created.", new JsonObject { ["event_id"] = id });
    }

    private static JsonObject ToJson(CalendarEventDraft draft) => new()
    {
        ["calendar_id"] = draft.CalendarId,
        ["title"] = draft.Title,
        ["starts_at"] = draft.StartsAt.ToUniversalTime().ToString("O"),
        ["ends_at"] = draft.EndsAt.ToUniversalTime().ToString("O"),
    };

    private static void Ensure(ConnectorSession session, string scope)
    {
        if (!session.Allows(scope))
        {
            throw new UnauthorizedAccessException("connector_scope_missing");
        }
    }

    private static void EnsureApproval(ConnectorSession session, ConnectorApproval approval, string hash)
    {
        if (!approval.Approved
            || approval.SessionId != session.SessionId
            || approval.ArgumentsHash != hash)
        {
            throw new UnauthorizedAccessException("connector_approval_invalid");
        }
    }
}
