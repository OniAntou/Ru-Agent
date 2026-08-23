using System.Collections.ObjectModel;

namespace RuApp.Services;

public enum RuAppMode
{
    Chat,
    Agent,
    Full,
}

public sealed class AgentSessionService
{
    private readonly ObservableCollection<string> _scopes = new();

    public bool IsEnabled { get; private set; }
    public bool HasActualGrant { get; private set; }
    public bool HasConfirmedFullAccess { get; private set; }
    public string? SessionId { get; private set; }
    public RuAppMode CurrentMode { get; private set; } = RuAppMode.Chat;
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
        BeginRequest(sessionId, RuAppMode.Agent);
    }

    public void BeginRequest(string sessionId, RuAppMode mode)
    {
        if (!string.Equals(SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Agent mode must use the observed conversation session.");
        }

        if (mode == RuAppMode.Chat)
        {
            throw new ArgumentException("Use Disable to return to chat mode.", nameof(mode));
        }

        CurrentMode = mode;
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

    public void ConfirmFullAccess()
    {
        HasConfirmedFullAccess = true;
    }

    public void Disable()
    {
        IsEnabled = false;
        CurrentMode = RuAppMode.Chat;
        ClearGrant();
    }

    private void ClearGrant()
    {
        HasActualGrant = false;
        _scopes.Clear();
    }
}
