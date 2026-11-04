using RuApp.Models;
using RuApp.Services;

namespace RuApp.ViewModels;

public sealed partial class SessionSidebarViewModel
{
    internal const int MaxWorkspaceDeletionSessions = 10_000;
    private const int MaxWorkspaceDeletionPages = 100;

    /// <summary>
    /// Loads the complete session listing and resolves the workspace group before the
    /// user confirms deletion. A missing page or unstable cursor fails closed.
    /// </summary>
    internal async Task<WorkspaceDeletionPlan?> PlanWorkspaceDeletionAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var appRoot = AppRoot;
        var currentOrganization = _organization.Current;
        var organization = new SidebarOrganizationPreferences
        {
            Groups = new Dictionary<string, string?>(currentOrganization.Groups, StringComparer.Ordinal),
            Order = currentOrganization.Order.ToList(),
        };
        var existingGroup = _groups.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, key, StringComparison.Ordinal));
        var localWorkspaces = Sessions.Where(session => !string.IsNullOrEmpty(session.WorkspacePath))
            .ToDictionary(session => session.Id,
                session => (Path: session.WorkspacePath, Kind: session.WorkspaceKind), StringComparer.Ordinal);
        var sessions = new List<SessionInfo>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var requestedCursors = new HashSet<string?>(StringComparer.Ordinal);
        string? cursor = null;
        var pageCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pageCount == MaxWorkspaceDeletionPages)
            {
                throw new InvalidOperationException("The complete session listing exceeds the deletion limit.");
            }

            if (!requestedCursors.Add(cursor))
            {
                throw new InvalidOperationException("The session listing repeated a page cursor.");
            }

            var page = await _backend.GetSessionsPageAsync(
                cursor, query: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page is null)
            {
                throw new InvalidOperationException("The complete session listing could not be loaded.");
            }
            pageCount++;

            foreach (var session in page.Sessions)
            {
                if (!seenIds.Add(session.Id))
                {
                    throw new InvalidOperationException("The session listing contained a duplicate session.");
                }

                if (sessions.Count == MaxWorkspaceDeletionSessions)
                {
                    throw new InvalidOperationException("The workspace contains too many chats to delete at once.");
                }

                sessions.Add(session);
            }

            cursor = page.NextCursor;
            if (cursor is null) break;
        }

        WorkspaceGroupIdentity GroupForListedSession(SessionInfo session)
        {
            // A draft's Host row exists before its first run records a workspace.
            // Use the frozen local hint only while durable workspace metadata is absent.
            if (string.IsNullOrEmpty(session.WorkspacePath) && session.WorkspaceKind != "scratch"
                && localWorkspaces.TryGetValue(session.Id, out var local))
                session = new SessionInfo { Id = session.Id, ClientSource = session.ClientSource,
                    WorkspacePath = local.Path, WorkspaceKind = local.Kind ?? session.WorkspaceKind };
            return SidebarSessionOrganization.GroupFor(session, appRoot, organization);
        }

        var matching = sessions.Where(session =>
                string.Equals(GroupForListedSession(session).Key, key, StringComparison.Ordinal))
            .ToArray();
        if (matching.Length == 0)
        {
            // Empty pinned projects still need a confirmed removal path.
            return existingGroup?.Path is null ? null : new WorkspaceDeletionPlan(
                key, existingGroup.Label, [], existingGroup.Path);
        }

        var identity = GroupForListedSession(matching[0]);
        return new WorkspaceDeletionPlan(
            key,
            existingGroup?.Label ?? identity.Label,
            matching.Select(session => session.Id).ToArray(),
            identity.Path);
    }

    /// <summary>
    /// Deletes every session of one workspace. Each delete still goes through the Host's
    /// single-session path, so scratch folders, approvals and tombstones keep their own rules;
    /// this method only batches work the user already confirmed and reports the counts. The
    /// user-selected project folder on disk is never deleted.
    /// </summary>
    internal async Task<WorkspaceDeletionResult> DeleteWorkspaceSessionsAsync(
        WorkspaceDeletionPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        long? activeDeletionGeneration = null;
        var deleted = 0;
        var partial = 0;
        var failed = 0;
        foreach (var sessionId in plan.SessionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await _backend.DeleteSessionAsync(sessionId).ConfigureAwait(true);
            if (outcome.Deleted || outcome.Partial)
            {
                deleted++;
                if (outcome.Partial) partial++;
                _drafts.Remove(sessionId);
                _organization.Remove(sessionId);
                if (string.Equals(ActiveSessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveSessionId = null;
                    ++_navigationGeneration;
                    activeDeletionGeneration = _navigationGeneration;
                }
                RemoveRow(sessionId);
            }
            else
            {
                failed++;
            }
        }

        await RefreshLatestAsync().ConfigureAwait(true);
        var workspaceRemoved = false;
        if (failed == 0 && plan.WorkspacePath is not null)
        {
            // A new chat may have arrived after confirmation, including beyond
            // the first visible page. Never delete it or hide its workspace.
            var remaining = await PlanWorkspaceDeletionAsync(plan.Key, cancellationToken).ConfigureAwait(true);
            if (remaining is null || remaining.SessionIds.Count == 0)
                workspaceRemoved = TryRemoveWorkspacePin(plan);
        }
        SessionDeletionFollowUp followUp = activeDeletionGeneration == _navigationGeneration
            && ActiveSessionId is null
            ? Sessions.Count > 0
                ? new SessionDeletionFollowUp.OpenSession(Sessions[0])
                : new SessionDeletionFollowUp.StartNewChat()
            : new SessionDeletionFollowUp.Nothing();
        return new WorkspaceDeletionResult(plan.SessionIds.Count, deleted, partial, failed, followUp, workspaceRemoved);
    }

    private bool TryRemoveWorkspacePin(WorkspaceDeletionPlan plan)
    {
        var pinnedPath = _pins.PinnedPaths.FirstOrDefault(path =>
            SidebarSessionOrganization.SamePath(path, plan.WorkspacePath));
        if (pinnedPath is not null && !_pins.Unpin(pinnedPath)) return false;
        if (SelectedWorkspaceKey == plan.Key) SelectedWorkspaceKey = null;
        _groupShowAll.Remove(plan.Key);
        Render();
        return true;
    }
}
