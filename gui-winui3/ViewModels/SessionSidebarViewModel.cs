using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

/// <summary>The Host calls the sidebar makes.</summary>
internal interface ISessionListBackend
{
    Task<SessionListResponse?> GetSessionsAsync(CancellationToken cancellationToken);

    Task<SessionInfo?> CreateSessionAsync();

    Task<ApiService.SessionDeletionOutcome> DeleteSessionAsync(string sessionId);
}

/// <summary>Resolves the current <see cref="ApiService"/> per call, so a reconnect needs no rewiring.</summary>
internal sealed class ApiSessionListBackend(Func<ApiService?> api) : ISessionListBackend
{
    public Task<SessionListResponse?> GetSessionsAsync(CancellationToken cancellationToken) =>
        api()?.GetSessionsAsync(cancellationToken) ?? Task.FromResult<SessionListResponse?>(null);

    public Task<SessionInfo?> CreateSessionAsync() =>
        api()?.CreateSessionAsync() ?? Task.FromResult<SessionInfo?>(null);

    public Task<ApiService.SessionDeletionOutcome> DeleteSessionAsync(string sessionId) =>
        api()?.DeleteSessionAsync(sessionId)
        ?? Task.FromResult(new ApiService.SessionDeletionOutcome(false, false, null));
}

/// <summary>The confirmations and notices a delete may need; the view owns the dialogs.</summary>
internal interface ISessionDeletionPrompts
{
    Task<bool> ConfirmDeleteAsync();

    Task ShowDeleteFailedAsync(string? message);

    /// <summary>The chat is gone but its artifacts are not; returns true to retry the cleanup.</summary>
    Task<bool> OfferCleanupRetryAsync(string? message);

    Task ShowCleanupUnconfirmedAsync();
}

/// <summary>What the shell should show after a delete.</summary>
internal abstract record SessionDeletionFollowUp
{
    internal sealed record Nothing : SessionDeletionFollowUp;

    internal sealed record OpenSession(SessionInfo Session) : SessionDeletionFollowUp;

    internal sealed record StartNewChat : SessionDeletionFollowUp;
}

/// <summary>
/// The result of a delete: what to show next and, for a partial delete, the Host's
/// message about leftover artifacts so the view can offer a cleanup retry afterwards.
/// </summary>
internal sealed record SessionDeletionResult(
    SessionDeletionFollowUp FollowUp, bool IsRemoved = false, bool IsPartial = false, string? PartialMessage = null);

/// <summary>A sidebar "move to" menu entry for one session.</summary>
internal sealed record SidebarMoveTarget(string Text, string SessionId, string GroupKey, string? GroupPath, string? BeforeId)
{
    internal bool IsSeparator => Text.Length == 0;
}

/// <summary>
/// The sidebar's session list: server rows merged with local drafts, workspace grouping,
/// pins, manual organization, refresh and the create/select/delete workflows.
/// </summary>
/// <remarks>
/// Call members on the UI thread. Refresh results arrive through the dispatch delegate.
/// Sidebar placement is presentation state only: it never changes a session's working
/// directory or execution permissions. Selection of the native list stays in the view,
/// which re-syncs it on <see cref="Rendered"/>.
/// </remarks>
public sealed partial class SessionSidebarViewModel : ObservableObject, IAsyncDisposable
{

    private readonly ISessionListBackend _backend;
    private readonly SidebarOrganizationStore _organization;
    private readonly WorkspaceGroupPreferenceStore _groupPreferences;
    private readonly WorkspacePinStore _pins;
    private readonly SessionWorkspaceMapStore _sessionWorkspaces;
    private readonly SessionDraftPresentation _drafts = new();
    private readonly HashSet<string> _groupShowAll = new(StringComparer.Ordinal);
    private readonly List<WorkspaceGroup> _groups = new();
    private readonly RefreshController<SessionListResponse?> _refresh;
    private long _navigationGeneration;
    private bool _disposed;

    internal SessionSidebarViewModel(
        ISessionListBackend backend,
        Action<Action> dispatch,
        SidebarOrganizationStore? organization = null,
        WorkspaceGroupPreferenceStore? groupPreferences = null,
        WorkspacePinStore? pins = null,
        SessionWorkspaceMapStore? sessionWorkspaces = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ArgumentNullException.ThrowIfNull(dispatch);
        _organization = organization ?? new SidebarOrganizationStore();
        _groupPreferences = groupPreferences ?? new WorkspaceGroupPreferenceStore();
        _pins = pins ?? new WorkspacePinStore();
        _sessionWorkspaces = sessionWorkspaces ?? new SessionWorkspaceMapStore();
        _refresh = new RefreshController<SessionListResponse?>(
            token => _backend.GetSessionsAsync(token),
            result => { if (result is not null) ApplySessions(result); },
            dispatch);
    }

    /// <summary>Raised after the rows were rebuilt; the view re-syncs list selection.</summary>
    public event Action? Rendered;

    /// <summary>Raised with a short status when a sidebar action fails.</summary>
    public event Action<string>? ActionFailed;

    /// <summary>Server sessions plus open or started drafts, newest first.</summary>
    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    /// <summary>The flattened sidebar: section headers, workspace groups, sessions and "show more" rows.</summary>
    public ObservableCollection<object> Rows { get; } = new();

    [ObservableProperty] public partial bool IsTruncated { get; private set; }

    /// <summary>The session open in the chat view.</summary>
    [ObservableProperty] public partial string? ActiveSessionId { get; private set; }

    /// <summary>The workspace group a new chat goes into.</summary>
    [ObservableProperty] public partial string? SelectedWorkspaceKey { get; private set; }

    /// <summary>The application's default workspace, where ungrouped sessions live.</summary>
    internal string? AppRoot { get; private set; }

    internal IReadOnlyList<WorkspaceGroup> Groups => _groups;

    internal void SetAppRoot(string? appRoot)
    {
        AppRoot = appRoot;
        Render();
    }

    internal void SetRefreshActive(bool active) => _refresh.SetActive(active);

    /// <summary>Periodic refresh; skipped while another refresh is in flight.</summary>
    internal Task<bool> RefreshAsync() => _refresh.RefreshAsync();

    /// <summary>
    /// Refresh after a local change. A poll already in flight may hold the list from
    /// before the change, so it is superseded rather than allowed to land.
    /// </summary>
    internal Task<bool> RefreshLatestAsync() => _refresh.RefreshLatestAsync();

    internal WorkspaceGroupIdentity GroupFor(SessionInfo session) =>
        SidebarSessionOrganization.GroupFor(session, AppRoot, _organization.Current);

    /// <summary>
    /// Merges a server listing into <see cref="Sessions"/> in place: rows keep their
    /// identity so containers and title waves survive, and the sidebar is rebuilt only
    /// when order, membership or row content changed.
    /// </summary>
    internal void ApplySessions(SessionListResponse result)
    {
        IsTruncated = result.SessionsTruncated;
        var before = SidebarRowFingerprint.ForSessions(Sessions);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int insertAt = 0;
        foreach (var incoming in _drafts.Project(result.Sessions))
        {
            seen.Add(incoming.Id);
            var existing = Sessions.FirstOrDefault(session => session.Id == incoming.Id);
            if (existing is null)
            {
                Sessions.Insert(insertAt, incoming);
            }
            else
            {
                existing.Title = incoming.Title;
                existing.MessageCount = incoming.MessageCount;
                existing.CreatedAt = incoming.CreatedAt;
                existing.UpdatedAt = incoming.UpdatedAt;
                existing.LastAgentActivityAt = incoming.LastAgentActivityAt;
                existing.IsAgentRunning = incoming.IsAgentRunning;
                existing.ParentSessionId = incoming.ParentSessionId;
                existing.ForkedFromMessageId = incoming.ForkedFromMessageId;
                existing.WorkspaceId = incoming.WorkspaceId;
                existing.ClientSource = incoming.ClientSource;
                existing.WorkspaceLabel = incoming.WorkspaceLabel;
                existing.WorkspacePath = incoming.WorkspacePath;
                int currentIndex = Sessions.IndexOf(existing);
                if (currentIndex != insertAt) Sessions.Move(currentIndex, insertAt);
            }

            insertAt++;
        }

        for (int index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!seen.Contains(Sessions[index].Id))
            {
                Sessions.RemoveAt(index);
            }
            else if (!string.IsNullOrEmpty(Sessions[index].WorkspacePath))
            {
                // The server now knows the workspace; the local fallback is no longer needed.
                _sessionWorkspaces.Remove(Sessions[index].Id);
            }
        }

        if (Rows.Count == 0 || !string.Equals(SidebarRowFingerprint.ForSessions(Sessions), before, StringComparison.Ordinal))
        {
            Render();
        }
    }

    /// <summary>Rebuilds the flattened rows from sessions, groups and pins.</summary>
    internal void Render()
    {
        var projection = SidebarSessionPresenter.Build(
            Sessions.ToList(),
            AppRoot,
            _organization.Current,
            _pins.PinnedPaths,
            ActiveSessionId,
            _groupPreferences.IsExpanded,
            key => _groupShowAll.Contains(key));

        _groups.Clear();
        _groups.AddRange(projection.Groups);
        ReconcileRows(projection.Rows);
        Rendered?.Invoke();
    }

    private static string RowKey(object row) => row switch
    {
        SessionInfo session => "session:" + session.Id,
        WorkspaceGroupRow group => "group:" + group.Group.Key,
        SidebarSectionRow section => "section:" + section.Key,
        SidebarEmptyRow empty => "empty:" + empty.Key,
        WorkspaceShowMoreRow more => "more:" + more.GroupKey,
        _ => throw new ArgumentException("Unknown sidebar row", nameof(row)),
    };

    /// <summary>
    /// Moves rows into place instead of clearing the collection: clearing destroys the
    /// source container and cancels a native drag in progress.
    /// </summary>
    private void ReconcileRows(IReadOnlyList<object> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var key = RowKey(desired[index]);
            var current = -1;
            for (var scan = index; scan < Rows.Count; scan++)
            {
                if (RowKey(Rows[scan]) == key) { current = scan; break; }
            }

            if (current < 0)
            {
                Rows.Insert(index, desired[index]);
            }
            else
            {
                if (current != index) Rows.Move(current, index);
                if (!Equals(Rows[index], desired[index])) Rows[index] = desired[index];
            }
        }

        while (Rows.Count > desired.Count) Rows.RemoveAt(Rows.Count - 1);
    }

    /// <summary>
    /// Flips a group's effective state and keeps the group key and the path key in step;
    /// a path flag left set by a pin would otherwise hold the group open forever.
    /// </summary>
    internal void ToggleGroup(string key, bool? boundIsExpanded)
    {
        var expanded = SidebarSessionPresenter.NextExpandedState(
            boundIsExpanded, _groupPreferences.IsExpanded(key, defaultExpanded: true));
        _groupPreferences.SetExpanded(key, expanded);
        if (ResolveWorkspacePath(key) is { Length: > 0 } path)
        {
            _groupPreferences.SetExpanded(SidebarSessionPresenter.PathStateKey(path), expanded);
        }

        Render();
    }

    internal void ToggleSection(string key)
    {
        var current = Rows.OfType<SidebarSectionRow>().FirstOrDefault(row => row.Key == key);
        _groupPreferences.SetExpanded(key, !(current?.IsExpanded ?? true));
        Render();
    }

    internal void ShowAll(string key)
    {
        _groupShowAll.Add(key);
        Render();
    }

    /// <summary>Removes a pinned workspace; its folder and sessions stay untouched.</summary>
    internal void Unpin(string key)
    {
        if (ResolveWorkspacePath(key) is not { } path) return;
        _pins.Unpin(path);
        if (SelectedWorkspaceKey == key) SelectedWorkspaceKey = null;
        Render();
    }

    internal void Pin(string folder)
    {
        var canonical = WorkspacePinStore.Canonicalize(folder);
        _pins.Pin(canonical);
        _groupPreferences.SetExpanded(SidebarSessionPresenter.PathStateKey(canonical), true);
        Render();
        var group = _groups.FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate.Path)
            && string.Equals(candidate.Path, canonical, StringComparison.OrdinalIgnoreCase));
        if (group is not null)
        {
            _groupPreferences.SetExpanded(group.Key, true);
            Render();
        }
    }

    /// <summary>Opens a collapsed group under a drag that has hovered over it.</summary>
    internal void ExpandForDrop(string key)
    {
        var collapsed = key is SidebarSessionOrganization.RecentsKey or SidebarSessionOrganization.CliKey
            ? Rows.OfType<SidebarSectionRow>().Any(row => row.Key == key && !row.IsExpanded)
            : Rows.OfType<WorkspaceGroupRow>().Any(row => row.Group.Key == key && !row.IsExpanded);
        if (!collapsed) return;
        _groupPreferences.SetExpanded(key, true);
        Render();
    }

    internal string? ResolveWorkspacePath(string key)
    {
        if (key == SidebarSessionOrganization.RecentsKey) return AppRoot;
        foreach (var group in _groups)
        {
            if (group.Key == key) return group.Path;
            if (!string.IsNullOrEmpty(group.Path) && SidebarSessionPresenter.PathStateKey(group.Path) == key) return group.Path;
        }

        return _pins.PinnedPaths.FirstOrDefault(pinned => SidebarSessionPresenter.PathStateKey(pinned) == key);
    }

    internal SidebarDropIntent? ResolveDrop(string draggedSessionId, object row, bool above) =>
        SidebarDragDropPresenter.Resolve(draggedSessionId, row, above, Sessions.ToList(), _groups, GroupFor);

    /// <summary>Moves a session within or between sidebar groups. Presentation only.</summary>
    internal bool Move(string sessionId, string key, string? path, string? beforeId)
    {
        if (!SidebarSessionPresenter.TryMove(
                _organization, Sessions.ToList(), _organization.Current, sessionId, key, path, beforeId))
        {
            ActionFailed?.Invoke("Could not save the sidebar order. Try again.");
            return false;
        }

        _groupPreferences.SetExpanded(key, true);
        _groupShowAll.Add(key);
        if (ActiveSessionId == sessionId) SelectedWorkspaceKey = key;
        Render();
        return true;
    }

    /// <summary>
    /// The "move to" entries for a session's menu: other groups (not for CLI sessions),
    /// then up/down within its own group. An empty text marks a separator.
    /// </summary>
    internal IReadOnlyList<SidebarMoveTarget> MoveTargets(string sessionId)
    {
        var row = Sessions.FirstOrDefault(session => session.Id == sessionId);
        if (row is null) return [];
        var targets = new List<SidebarMoveTarget>();
        if (row.ClientSource != "cli")
        {
            targets.Add(new("Recents", sessionId, SidebarSessionOrganization.RecentsKey, null, null));
            foreach (var group in _groups.Where(group => group.Path is not null))
            {
                targets.Add(new(group.Label, sessionId, group.Key, group.Path, null));
            }
        }

        var identity = GroupFor(row);
        var siblings = _groups.FirstOrDefault(group => group.Key == identity.Key)?.Sessions.ToList();
        var index = siblings?.FindIndex(session => session.Id == sessionId) ?? -1;
        if (siblings is null || index < 0) return targets;
        if (targets.Count > 0 && siblings.Count > 1) targets.Add(new(string.Empty, sessionId, string.Empty, null, null));
        if (index > 0) targets.Add(new("Move up", sessionId, identity.Key, identity.Path, siblings[index - 1].Id));
        if (index < siblings.Count - 1)
        {
            targets.Add(new("Move down", sessionId, identity.Key, identity.Path,
                index + 2 < siblings.Count ? siblings[index + 2].Id : null));
        }

        return targets;
    }

    /// <summary>
    /// Makes a session the open one and drops every unstarted draft other than it. An
    /// empty draft is visible only while open; hiding it does not delete the Host record.
    /// </summary>
    internal void ActivateDrafts(string? sessionId)
    {
        if (sessionId is not null) ActiveSessionId = sessionId;
        var discarded = _drafts.Activate(sessionId);
        if (ActiveSessionId is not null && discarded.Contains(ActiveSessionId)) ActiveSessionId = null;
        foreach (var id in discarded)
        {
            var row = Sessions.FirstOrDefault(session => session.Id == id);
            if (row is not null) Sessions.Remove(row);
            _sessionWorkspaces.Remove(id);
            if (_organization.Current.Groups.ContainsKey(id)) _organization.Remove(id);
        }

        if (discarded.Count > 0) Render();
    }

    /// <summary>Leaving the chat surface: a later create or open must not be overtaken.</summary>
    internal void LeaveChat()
    {
        ++_navigationGeneration;
        ActivateDrafts(null);
    }

    /// <summary>The workspace a new chat starts in, from the selected group.</summary>
    internal (string? Key, string? Path) NewChatTarget()
    {
        var key = SidebarSessionPresenter.ResolveNewChatWorkspace(SelectedWorkspaceKey);
        return (key, key is null ? null : ResolveWorkspacePath(key));
    }

    /// <summary>
    /// Creates a Host session and shows it as an open draft. Returns null when the create
    /// failed or a newer navigation took over while it was in flight.
    /// </summary>
    internal async Task<SessionInfo?> CreateDraftAsync(string? workspaceKey, string? workspacePath)
    {
        var navigation = ++_navigationGeneration;
        var session = await _backend.CreateSessionAsync();
        if (_disposed || navigation != _navigationGeneration) return null;
        if (session is null)
        {
            ActionFailed?.Invoke("Could not create a new chat session");
            return null;
        }

        ActivateDrafts(session.Id);
        _drafts.Register(session, workspaceKey, workspacePath);
        if (workspacePath is not null) _sessionWorkspaces.Set(session.Id, workspacePath);
        Sessions.Insert(0, session);
        SelectedWorkspaceKey = workspaceKey;
        _groupPreferences.SetExpanded(GroupFor(session).Key, true);
        Render();
        return session;
    }

    /// <summary>The first accepted prompt titles a draft and keeps it after it is left.</summary>
    internal void MarkStarted(string sessionId, string prompt)
    {
        if (_drafts.MarkStarted(sessionId, prompt)) Render();
    }

    /// <summary>
    /// Opens a session row. Returns the workspace the chat should run in: the server's
    /// when known, otherwise the one recorded when the draft was created.
    /// </summary>
    internal string? Select(SessionInfo session)
    {
        ++_navigationGeneration;
        ActivateDrafts(session.Id);
        var key = GroupFor(session).Key;
        SelectedWorkspaceKey = key;
        if (!_groupPreferences.IsExpanded(key, defaultExpanded: true))
        {
            _groupPreferences.SetExpanded(key, true);
            Render();
        }

        return !string.IsNullOrEmpty(session.WorkspacePath) ? session.WorkspacePath : _sessionWorkspaces.Lookup(session.Id);
    }

    /// <summary>Selects a workspace group for new chats; selecting it again clears it. Returns whether it is now selected.</summary>
    internal bool SelectGroup(string key)
    {
        SelectedWorkspaceKey = SelectedWorkspaceKey == key ? null : key;
        return SelectedWorkspaceKey is not null;
    }

    /// <summary>The session the chat records as open; keeps the sidebar highlight in step.</summary>
    internal void NoteOpened(string sessionId)
    {
        ActiveSessionId = sessionId;
    }

    /// <summary>
    /// Deletes a session after confirmation. The row disappears as soon as the Host
    /// confirms; an HTTP success alone is not deletion proof. A partial delete (chat gone,
    /// artifacts left) is reported as deleted with an offer to retry the cleanup.
    /// </summary>
    internal async Task<SessionDeletionResult> DeleteAsync(
        string sessionId, string? openSessionId, ISessionDeletionPrompts prompts)
    {
        if (!await prompts.ConfirmDeleteAsync()) return new(new SessionDeletionFollowUp.Nothing());

        var outcome = await _backend.DeleteSessionAsync(sessionId);
        if (!outcome.Deleted && !outcome.Partial)
        {
            await prompts.ShowDeleteFailedAsync(outcome.Message);
            return new(new SessionDeletionFollowUp.Nothing());
        }

        bool wasOpen = string.Equals(openSessionId, sessionId, StringComparison.OrdinalIgnoreCase);
        if (outcome.Deleted)
        {
            _drafts.Remove(sessionId);
            _organization.Remove(sessionId);
        }

        RemoveRow(sessionId);
        await RefreshLatestAsync();

        SessionDeletionFollowUp followUp = wasOpen && Sessions.Count > 0
            ? new SessionDeletionFollowUp.OpenSession(Sessions[0])
            : Sessions.Count == 0 ? new SessionDeletionFollowUp.StartNewChat() : new SessionDeletionFollowUp.Nothing();
        // The chat is gone either way; the cleanup offer comes after the shell moved on.
        return new(followUp, IsRemoved: true, outcome.Partial, outcome.Message);
    }

    private void RemoveRow(string sessionId)
    {
        var row = Sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        Sessions.Remove(row);
        Render();
    }

    /// <summary>
    /// Re-issues the same delete while the Host keeps reporting a partial cleanup. Once
    /// the session record is gone the Host can no longer say "partial", so a retry that
    /// is not partial ends as unconfirmed rather than as success or a failed delete.
    /// </summary>
    internal async Task RetryCleanupAsync(string sessionId, string? message, ISessionDeletionPrompts prompts)
    {
        while (await prompts.OfferCleanupRetryAsync(message))
        {
            var retry = await _backend.DeleteSessionAsync(sessionId);
            if (retry.Deleted) return;
            if (!retry.Partial)
            {
                await prompts.ShowCleanupUnconfirmedAsync();
                return;
            }

            message = retry.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _refresh.DisposeAsync();
    }
}
