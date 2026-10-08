namespace RuApp.Services;

/// <summary>
/// Which tab of the right side surface a session has open.
/// </summary>
public enum SidePanelTabKind
{
    Changes,
    Tasks,
    Files,
}

/// <summary>
/// The right side surface's shape for one session: the floating summary card,
/// the full-height details panel, or nothing.
/// </summary>
public enum AgentSidePanelMode
{
    Card,
    Panel,
    Hidden,
}

/// <summary>
/// The per-session state of the right side surface: which host is showing,
/// which tabs are open, which one is active, the fullscreen flag, and the width
/// clamp. One bounded entry per session, so a closed tab or a hidden surface
/// survives a session switch without outliving its session.
/// </summary>
public sealed class AgentSidePanelPolicy
{
    public const int MaxRetainedSessions = 8;
    public const double MinimumWidth = 280;
    public const double MaximumWidth = 560;
    public const double DefaultWidth = 360;

    private sealed class SessionState
    {
        internal AgentSidePanelMode Mode = AgentSidePanelMode.Card;
        internal AgentSidePanelMode LastVisibleMode = AgentSidePanelMode.Card;
        internal List<SidePanelTabKind> OpenTabs = new() { SidePanelTabKind.Changes, SidePanelTabKind.Tasks, SidePanelTabKind.Files };
        internal SidePanelTabKind ActiveTab = SidePanelTabKind.Files;
        internal bool Fullscreen;
        internal long Touched;
    }

    private readonly Dictionary<string, SessionState> _bySession = new(StringComparer.Ordinal);
    private long _touchCounter;

    /// <summary>Clamps a stored or dragged width; a non-finite value reads as the default.</summary>
    public static double ClampWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinimumWidth, MaximumWidth) : DefaultWidth;

    public AgentSidePanelMode GetMode(string? sessionId) =>
        Find(sessionId)?.Mode ?? AgentSidePanelMode.Card;

    public IReadOnlyList<SidePanelTabKind> GetOpenTabs(string? sessionId) =>
        Find(sessionId)?.OpenTabs.ToArray() ?? DefaultOpenTabs();

    public SidePanelTabKind GetActiveTab(string? sessionId) =>
        Find(sessionId)?.ActiveTab ?? SidePanelTabKind.Files;

    public bool IsFullscreen(string? sessionId) => Find(sessionId)?.Fullscreen ?? false;

    public void ExpandToPanel(string? sessionId)
    {
        var state = GetOrCreate(sessionId);
        if (state is null)
        {
            return;
        }

        if (state.OpenTabs.Count == 0)
        {
            state.OpenTabs.Add(SidePanelTabKind.Files);
            state.ActiveTab = SidePanelTabKind.Files;
        }

        state.Mode = AgentSidePanelMode.Panel;
        state.LastVisibleMode = AgentSidePanelMode.Panel;
    }

    public void CollapseToCard(string? sessionId)
    {
        var state = GetOrCreate(sessionId);
        if (state is null)
        {
            return;
        }

        state.Mode = AgentSidePanelMode.Card;
        state.LastVisibleMode = AgentSidePanelMode.Card;
        state.Fullscreen = false;
    }

    public void ToggleVisibility(string? sessionId)
    {
        var state = GetOrCreate(sessionId);
        if (state is null)
        {
            return;
        }

        if (state.Mode == AgentSidePanelMode.Hidden)
        {
            state.Mode = state.LastVisibleMode;
            return;
        }

        state.LastVisibleMode = state.Mode;
        state.Mode = AgentSidePanelMode.Hidden;
    }

    public void ActivateTab(string? sessionId, SidePanelTabKind kind)
    {
        var state = GetOrCreate(sessionId);
        if (state is null || !state.OpenTabs.Contains(kind))
        {
            return;
        }

        state.ActiveTab = kind;
    }

    public void CloseTab(string? sessionId, SidePanelTabKind kind)
    {
        var state = GetOrCreate(sessionId);
        if (state is null || !state.OpenTabs.Remove(kind))
        {
            return;
        }

        if (state.ActiveTab == kind)
        {
            state.ActiveTab = state.OpenTabs.Count > 0 ? state.OpenTabs[0] : SidePanelTabKind.Changes;
        }

        if (state.OpenTabs.Count == 0)
        {
            state.Mode = AgentSidePanelMode.Card;
            state.LastVisibleMode = AgentSidePanelMode.Card;
            state.Fullscreen = false;
        }
    }

    public void OpenTab(string? sessionId, SidePanelTabKind kind)
    {
        var state = GetOrCreate(sessionId);
        if (state is null)
        {
            return;
        }

        if (!state.OpenTabs.Contains(kind))
        {
            state.OpenTabs.Add(kind);
        }

        state.ActiveTab = kind;
    }

    public void ToggleFullscreen(string? sessionId)
    {
        var state = GetOrCreate(sessionId);
        if (state is null)
        {
            return;
        }

        state.Fullscreen = !state.Fullscreen;
    }

    public void Remove(string? sessionId)
    {
        if (IsUsableSessionId(sessionId))
        {
            _bySession.Remove(sessionId!);
        }
    }

    private SessionState? Find(string? sessionId) =>
        IsUsableSessionId(sessionId) && _bySession.TryGetValue(sessionId!, out var state) ? state : null;

    private SessionState? GetOrCreate(string? sessionId)
    {
        if (!IsUsableSessionId(sessionId))
        {
            return null;
        }

        if (!_bySession.TryGetValue(sessionId!, out var state))
        {
            EvictOldestWhileFull();
            state = new SessionState { Touched = ++_touchCounter };
            _bySession[sessionId!] = state;
            return state;
        }

        state.Touched = ++_touchCounter;
        return state;
    }

    private void EvictOldestWhileFull()
    {
        while (_bySession.Count >= MaxRetainedSessions)
        {
            string? oldestKey = null;
            long oldestTouch = long.MaxValue;
            foreach (var (key, value) in _bySession)
            {
                if (value.Touched < oldestTouch)
                {
                    oldestTouch = value.Touched;
                    oldestKey = key;
                }
            }

            if (oldestKey is null)
            {
                return;
            }

            _bySession.Remove(oldestKey);
        }
    }

    private static bool IsUsableSessionId(string? sessionId) => !string.IsNullOrWhiteSpace(sessionId);

    private static IReadOnlyList<SidePanelTabKind> DefaultOpenTabs() =>
        new[] { SidePanelTabKind.Changes, SidePanelTabKind.Tasks, SidePanelTabKind.Files };
}
