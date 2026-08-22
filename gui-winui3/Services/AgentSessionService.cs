using System.Collections.ObjectModel;

namespace RuApp.Services;

public sealed class AgentSessionService
{
    private readonly ObservableCollection<string> _scopes = new();

    public bool IsEnabled { get; private set; }
    public bool HasActualGrant { get; private set; }
    public string? SessionId { get; private set; }
    public ReadOnlyObservableCollection<string> Scopes { get; }

    public AgentSessionService()
    {
        Scopes = new ReadOnlyObservableCollection<string>(_scopes);
    }

    public void ObserveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Conversation session ID is required.", nameof(sessionId));
        }

        if (string.Equals(SessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        SessionId = sessionId;
        ClearGrant();
    }

    public void BeginRequest(string sessionId)
    {
        if (!string.Equals(SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Agent mode must use the observed conversation session.");
        }

        IsEnabled = true;
        ClearGrant();
    }

    public void ApplyGrant(AgentSessionGrantModel grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (!string.Equals(SessionId, grant.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Agent grant does not match the observed conversation session.");
        }

        _scopes.Clear();
        foreach (var scope in grant.Scopes)
        {
            _scopes.Add(scope);
        }
        HasActualGrant = true;
    }

    public void Disable()
    {
        IsEnabled = false;
        ClearGrant();
    }

    private void ClearGrant()
    {
        HasActualGrant = false;
        _scopes.Clear();
    }
}
