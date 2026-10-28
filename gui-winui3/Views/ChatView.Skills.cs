using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuAgent.Client;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    private SkillsApiClient? _skillsApiClient;
    private bool _skillsDialogOpen;

    private async void OnSkillsButtonClick(object sender, RoutedEventArgs e)
    {
        if (_skillsDialogOpen) return;
        _skillsDialogOpen = true;
        try { await ShowSkillsAsync(); }
        catch (Exception ex) { GuiDiagnosticWriter.AppendTraceLine($"Skills dialog failed: {ex.Message}"); }
        finally { _skillsDialogOpen = false; }
    }

    private async Task ShowSkillsAsync()
    {
        if (_apiService is null || string.IsNullOrWhiteSpace(_agentWorkspace)) return;
        var service = _apiService;
        var workspace = _agentWorkspace;
        var session = _activeSessionId;
        var skillsClient = _skillsApiClient ??= service.CreateSkillsApiClient();
        bool OwnsView() => !_scrollingDisposed && ReferenceEquals(_apiService, service)
            && _agentWorkspace == workspace && _activeSessionId == session;
        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource? searchCancellation = null;

        var search = new TextBox { PlaceholderText = "Search skills", Margin = new Thickness(0, 0, 0, 8) };
        var rootPath = new TextBox { PlaceholderText = "Existing local skill root path", Margin = new Thickness(0, 8, 0, 4) };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        var items = new StackPanel { Spacing = 6 };
        var loadGeneration = 0;
        var closed = false;
        var list = new ScrollViewer { Content = items, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = new ContentDialog
        {
            Title = "Skills",
            PrimaryButtonText = "Add root",
            SecondaryButtonText = "Remove root",
            CloseButtonText = "Done",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    search,
                    new Button { Content = "Refresh catalog", HorizontalAlignment = HorizontalAlignment.Left, Tag = "refresh" },
                    rootPath,
                    new ScrollViewer { Content = status, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    list,
                },
            },
        };

        var refreshButton = (Button)((StackPanel)dialog.Content).Children[1];
        refreshButton.Click += async (_, _) => await LoadAsync(refresh: true);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (string.IsNullOrWhiteSpace(rootPath.Text)) { status.Text = "Enter an existing local folder path."; return; }
            await UpdateAsync(new(workspace, "add_root", Root: rootPath.Text.Trim()));
            rootPath.Text = string.Empty;
        };
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (string.IsNullOrWhiteSpace(rootPath.Text)) { status.Text = "Enter the registered root path to remove."; return; }
            await UpdateAsync(new(workspace, "remove_root", Root: rootPath.Text.Trim()));
            rootPath.Text = string.Empty;
        };
        search.TextChanged += async (_, _) =>
        {
            searchCancellation?.Cancel();
            searchCancellation?.Dispose();
            searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var token = searchCancellation.Token;
            try { await Task.Delay(180, token); await LoadAsync(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        };
        dialog.Closed += (_, _) => { closed = true; ++loadGeneration; lifetime.Cancel(); };

        async Task LoadAsync(bool refresh = false)
        {
            var generation = ++loadGeneration;
            try
            {
                var result = await skillsClient.ListAsync(workspace, search.Text, refresh, lifetime.Token);
                if (closed || !OwnsView() || generation != loadGeneration) return;
                items.Children.Clear();
                foreach (var skill in result.Items)
                {
                    var row = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 8) };
                    var choose = new Button
                    {
                        Content = new TextBlock { Text = $"{skill.Name}  /skill {skill.Id}\n{skill.Description}", TextWrapping = TextWrapping.Wrap,
                            MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis },
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        IsEnabled = skill.Enabled && skill.UserInvocable,
                    };
                    ToolTipService.SetToolTip(choose, skill.UserInvocable
                        ? skill.Description
                        : "This skill does not allow user invocation.");
                    choose.Click += (_, _) =>
                    {
                        if (!OwnsView()) { dialog.Hide(); return; }
                        var draft = ChatInputTextBox.Text.Trim();
                        if (draft.StartsWith("/skill ", StringComparison.OrdinalIgnoreCase))
                        {
                            var argumentStart = draft.IndexOf(' ', 7);
                            draft = argumentStart < 0 ? string.Empty : draft[(argumentStart + 1)..];
                        }
                        ChatInputTextBox.Text = $"/skill {skill.Id} {draft}";
                        ChatInputTextBox.Focus(FocusState.Programmatic);
                        dialog.Hide();
                    };
                    var enabled = new ToggleSwitch { IsOn = skill.Enabled, Header = "Enabled", MinWidth = 84 };
                    enabled.Toggled += async (_, _) => await UpdateAsync(new(workspace, "set_enabled", Id: skill.Id, Enabled: enabled.IsOn));
                    var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    var implicitButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    var setImplicit = new Button { Content = "Allow auto" };
                    setImplicit.Click += async (_, _) => await UpdateAsync(new(workspace, "set_implicit", Id: skill.Id, Enabled: true));
                    var clearImplicit = new Button { Content = "Manual only" };
                    clearImplicit.Click += async (_, _) => await UpdateAsync(new(workspace, "set_implicit", Id: skill.Id, Enabled: false));
                    implicitButtons.Children.Add(setImplicit);
                    implicitButtons.Children.Add(clearImplicit);
                    row.Children.Add(choose);
                    controls.Children.Add(enabled);
                    controls.Children.Add(implicitButtons);
                    row.Children.Add(controls);
                    row.Children.Add(new TextBlock { Text = skill.ModelInvocable ? "Automatic selection allowed" : "Manual invocation only", Opacity = 0.7 });
                    items.Children.Add(row);
                }
                var messages = result.Diagnostics.Select(d => $"{d.Code}: {d.SourcePath}").ToList();
                if (result.Truncated) messages.Add($"Showing {result.Items.Count} of {result.Total} skills.");
                status.Text = string.Join(Environment.NewLine, messages);
            }
            catch (Exception ex)
            {
                if (closed || generation != loadGeneration) return;
                status.Text = $"Could not load skills: {ex.Message}";
            }
        }

        async Task UpdateAsync(SkillSettingsRequest request)
        {
            if (closed || !OwnsView()) return;
            try
            {
                var result = await skillsClient.UpdateAsync(request, lifetime.Token);
                if (closed) return;
                items.Children.Clear();
                // Requery with the current search so every row reflects the Host's saved state.
                await LoadAsync();
                if (result.Diagnostics.Count > 0)
                    status.Text = string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.SourcePath}"));
            }
            catch (Exception ex) { if (!closed) status.Text = $"Could not update skills: {ex.Message}"; }
        }

        dialog.Opened += async (_, _) => await LoadAsync();
        try { await ContentDialogGate.ShowAsync(dialog, OwnsView); }
        finally { lifetime.Cancel(); searchCancellation?.Cancel(); searchCancellation?.Dispose(); }
    }
}
