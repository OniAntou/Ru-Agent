using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using RuApp.Services;
using RuApp.Views;

namespace RuApp;

public sealed partial class MainWindow
{
    private void LeaveChatSession() => Sidebar.LeaveChat();

    /// <summary>Creates a session in a workspace and opens it as a draft in the chat view.</summary>
    private async Task CreateDraftSessionAsync(string? workspaceKey, string? workspacePath)
    {
        if (_apiService is null || _chatView is null) return;
        var session = await Sidebar.CreateDraftAsync(workspaceKey, workspacePath);
        // A late create must not take over a newer create or session selection.
        if (_closed || session is null) return;

        _chatView.SetRequestedWorkspace(workspacePath);
        OnNavClick(NavChat, new RoutedEventArgs());
        await _chatView.OpenSessionAsync(session.Id, SessionDraftPresentation.PlaceholderTitle);
    }

    private void OnChatSessionStarted(object? sender, SessionStartedEventArgs e) => Sidebar.MarkStarted(e.SessionId, e.Prompt);
}
