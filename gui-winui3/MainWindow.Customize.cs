using Microsoft.UI.Xaml;
using RuApp.Views;

namespace RuApp;

/// <summary>
/// The Customize page (skills and plugins), opened from the sidebar entry under New
/// Chat, from <c>/skills</c> in the composer, and left again for the chat.
/// </summary>
public sealed partial class MainWindow
{
    private CustomizeView? _customizeView;

    private void CreateCustomizeView()
    {
        if (_apiService is null) return;
        _customizeView = new CustomizeView();
        _customizeView.Initialize(_apiService, () => _chatView?.CurrentWorkspace);
        _customizeView.UseSkillRequested += OnUseSkillRequested;
        _customizeView.SetActive(false);
        if (_chatView is not null) _chatView.CustomizeRequested += OnChatCustomizeRequested;
    }

    private void OnChatCustomizeRequested(object? sender, EventArgs e) =>
        OnNavClick(NavCustomize, new RoutedEventArgs());

    /// <summary>Back to the chat that was open, with the skill's command in the composer.</summary>
    private void OnUseSkillRequested(object? sender, string skillId)
    {
        OnNavClick(NavChat, new RoutedEventArgs());
        _chatView?.InsertSkillCommand(skillId);
    }
}
