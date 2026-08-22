using System.Collections.ObjectModel;

namespace RuApp.Services;

public sealed class AgentSessionService
{
    private readonly ObservableCollection<string> _scopes = new();

    public bool IsEnabled { get; private set; }
    public string? SessionId { get; private set; }
    public ReadOnlyObservableCollection<string> Scopes { get; }

    public AgentSessionService()
    {
        Scopes = new ReadOnlyObservableCollection<string>(_scopes);
    }

    public void Enable(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Agent session ID is required.", nameof(sessionId));
        }

        IsEnabled = true;
        SessionId = sessionId;
        _scopes.Clear();
        _scopes.Add("workspace.read");
        _scopes.Add("system.read");
        _scopes.Add("terminal.execute");
    }

    public void Disable()
    {
        IsEnabled = false;
        SessionId = null;
        _scopes.Clear();
    }
}
