using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using RuApp.Services;
using RuApp.Views;

namespace RuApp;

public sealed partial class MainWindow
{
    private readonly SessionDraftPresentation _sessionDrafts = new();
    private long _sessionNavigationGeneration;

    private void ActivateSessionDrafts(string? sessionId)
    {
        if (sessionId is not null) _activeSessionId = sessionId;
        var discarded = _sessionDrafts.Activate(sessionId);
        if (_activeSessionId is not null && discarded.Contains(_activeSessionId)) _activeSessionId = null;
        foreach (var id in discarded)
        {
            var row = Sessions.FirstOrDefault(session => session.Id == id);
            if (row is not null) Sessions.Remove(row);
            _sessionWorkspaces.Remove(id);
            if (_sidebarOrganization.Current.Groups.ContainsKey(id)) _sidebarOrganization.Remove(id);
        }
        if (discarded.Count > 0) RenderWorkspaceSidebar();
    }

    private void LeaveChatSession()
    {
        ++_sessionNavigationGeneration;
        ActivateSessionDrafts(null);
    }

    private async Task CreateDraftSessionAsync(string? workspaceKey, string? workspacePath)
    {
        if (_apiService is null || _chatView is null) return;
        var navigationGeneration = ++_sessionNavigationGeneration;
        var session = await _apiService.CreateSessionAsync();
        // A late create must not take over a newer create or session selection.
        if (_closed || navigationGeneration != _sessionNavigationGeneration) return;
        if (session is null)
        {
            SetSessionActionError("Could not create a new chat session");
            return;
        }

        ActivateSessionDrafts(session.Id);
        _sessionDrafts.Register(session, workspaceKey, workspacePath);
        if (workspacePath is not null) _sessionWorkspaces.Set(session.Id, workspacePath);
        Sessions.Insert(0, session);
        _selectedWorkspaceKey = workspaceKey;
        _workspaceGroups.SetExpanded(SidebarGroupFor(session).Key, true);
        ClearWorkspaceSearch();
        _chatView.SetRequestedWorkspace(workspacePath);
        RenderWorkspaceSidebar();
        OnNavClick(NavChat, new RoutedEventArgs());
        await _chatView.OpenSessionAsync(session.Id, SessionDraftPresentation.PlaceholderTitle);
    }

    private void OnChatSessionStarted(object? sender, SessionStartedEventArgs e)
    {
        if (_sessionDrafts.MarkStarted(e.SessionId, e.Prompt)) RenderWorkspaceSidebar();
    }
}
