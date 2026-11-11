using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuAgent.Client;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The Skills tab: the workspace skill catalog with enable, automatic-use and run
/// controls, and adding or removing local skill folders.
/// </summary>
public sealed partial class CustomizeView
{
    private SkillsApiClient? _skills;
    private CancellationTokenSource? _skillWork;
    private CancellationTokenSource? _searchDelay;
    private IReadOnlyList<SkillCatalogItem>? _renderedSkills;
    private bool _rendering;

    private CancellationToken BeginSkillWork()
    {
        _skillWork ??= new CancellationTokenSource();
        return _skillWork.Token;
    }

    private void CancelSkillWork()
    {
        _searchDelay?.Cancel();
        _skillWork?.Cancel();
        _skillWork?.Dispose();
        _skillWork = null;
    }

    private bool TryGetSkillsTarget(out SkillsApiClient skills, out string workspace)
    {
        skills = null!;
        workspace = _workspace() ?? string.Empty;
        if (_api is null) return false;
        if (string.IsNullOrWhiteSpace(workspace))
        {
            ShowStatus("Open a workspace in chat to change skill availability or registered folders.");
            return false;
        }
        skills = _skills ??= _api.CreateSkillsApiClient();
        return true;
    }

    private Task LoadSkillsAsync(bool refresh = false) => _skillsLoader.LoadAsync(SearchBox.Text, refresh);

    private void ApplySkillsState(CustomizeSkillsState state)
    {
        if (!_active) return;
        var hasWorkspace = !string.IsNullOrWhiteSpace(state.Workspace);
        // The subtitle describes the Skills tab; the other tabs set their own.
        if (SkillsTab.IsChecked == true)
            WorkspaceText.Text = hasWorkspace
                ? (_workspaceIsScratch() ? "Skills for this chat · no project folder chosen" : $"Skills for {state.Workspace}")
                : "User skills · open a workspace to change availability or add folders.";
        AddButton.IsEnabled = hasWorkspace;
        var showSkeleton = state.IsLoading && state.Items.Count == 0;
        SkillsSkeleton.Visibility = showSkeleton ? Visibility.Visible : Visibility.Collapsed;
        SkillsList.Visibility = showSkeleton ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(_renderedSkills, state.Items))
        {
            _renderedSkills = state.Items;
            Render(state.Items, hasWorkspace);
        }
        SkillsEmptyText.Text = hasWorkspace ? "No skills found. Add a local skill folder with Add." : "No user skills found.";
        SkillsEmptyText.Visibility = !state.IsLoading && state.Items.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        if (SkillsTab.IsChecked == true) ShowStatus(state.Status, state.Details);
    }

    private void Render(IReadOnlyList<SkillCatalogItem> items, bool canManage)
    {
        _rendering = true;
        try
        {
            SkillsList.ItemsSource = items.Select(item => new CustomizeSkillRow(item, canManage)).ToList();
        }
        finally
        {
            // Template toggles apply their bound state during layout, after this call.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _rendering = false);
        }
        SkillsEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task UpdateSkillsAsync(SkillSettingsRequest request)
    {
        if (!TryGetSkillsTarget(out var skills, out _)) return;
        try
        {
            await skills.UpdateAsync(request, BeginSkillWork());
            // Requery with the current search so each row shows the Host's saved state.
            await LoadSkillsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowStatus($"Could not update skills: {error.Message}"); }
    }

    private async void OnSkillEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_rendering || sender is not ToggleSwitch { Tag: CustomizeSkillRow row } toggle || toggle.IsOn == row.Enabled) return;
        if (!TryGetSkillsTarget(out _, out var workspace)) return;
        await UpdateSkillsAsync(new SkillSettingsRequest(workspace, "set_enabled", Id: row.Id, Enabled: toggle.IsOn));
    }

    private void OnSkillMenuOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menu) return;
        foreach (var item in menu.Items.OfType<ToggleMenuFlyoutItem>())
            if (item.Tag is CustomizeSkillRow row) item.IsChecked = row.Skill.ModelInvocable;
    }

    private async void OnSkillAutomaticClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: CustomizeSkillRow row } item) return;
        if (!TryGetSkillsTarget(out _, out var workspace)) return;
        await UpdateSkillsAsync(new SkillSettingsRequest(workspace, "set_implicit", Id: row.Id, Enabled: item.IsChecked));
    }

    private void OnUseSkillClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CustomizeSkillRow { CanUse: true } row })
            UseSkillRequested?.Invoke(this, row.Id);
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchDelay?.Cancel();
        _searchDelay?.Dispose();
        _searchDelay = new CancellationTokenSource();
        var token = _searchDelay.Token;
        try
        {
            await Task.Delay(180, token);
            await LoadSkillsAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await LoadSkillsAsync(refresh: true);

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetSkillsTarget(out _, out var workspace)) return;
        var path = await AskAsync("Add a skill folder", "Existing local folder that holds skill folders", "Add");
        if (path is not null) await UpdateSkillsAsync(new SkillSettingsRequest(workspace, "add_root", Root: path));
    }

    private async void OnRemoveFolderClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetSkillsTarget(out _, out var workspace)) return;
        var path = await AskAsync("Remove a skill folder", "The registered folder to stop reading skills from", "Remove");
        if (path is not null) await UpdateSkillsAsync(new SkillSettingsRequest(workspace, "remove_root", Root: path));
    }

    /// <summary>A one-line prompt; null when cancelled or left empty.</summary>
    private async Task<string?> AskAsync(string title, string placeholder, string confirm)
    {
        var input = new TextBox { PlaceholderText = placeholder, MinWidth = 420 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = confirm,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var result = await ContentDialogGate.ShowAsync(dialog, () => _active);
        return result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }
}
