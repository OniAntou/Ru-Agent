using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The Customize page: the workspace's skills and Ru's plugins, opened from the
/// sidebar in place of the chat. The Host owns every setting shown here; the page
/// reads it on activation and writes each change back through the API.
/// </summary>
public sealed partial class CustomizeView : UserControl, IDisposable
{
    private ApiService? _api;
    private Func<string?> _workspace = () => null;
    private bool _active;
    private readonly CustomizeSkillsLoader _skillsLoader;
    private long _activationGeneration;

    public CustomizeView()
    {
        InitializeComponent();
        SkillsSkeleton.ItemsSource = Enumerable.Range(0, 4);
        _skillsLoader = new CustomizeSkillsLoader(action =>
        {
            if (DispatcherQueue.HasThreadAccess) action();
            else DispatcherQueue.TryEnqueue(() => action());
        });
        _skillsLoader.StateChanged += (_, state) => ApplySkillsState(state);
        Loaded += OnCustomizeLoaded;
    }

    /// <summary>A skill the user chose to run; the shell returns to chat with it typed.</summary>
    public event EventHandler<string>? UseSkillRequested;

    public void Initialize(ApiService apiService, Func<string?> workspace)
    {
        CancelSkillWork();
        _activationGeneration++;
        _api = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        // A reconnected Host is a new session: the old skills client is bound to the last one.
        _skills?.Dispose();
        var skills = _skills = _api.CreateSkillsApiClient();
        _skillsLoader.Attach(skills.ListAsync, _workspace);
        if (_active) _ = LoadComputerUseAsync();
    }

    /// <summary>Loads fresh state each time the page is shown; hidden, it cancels its work.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        _activationGeneration++;
        _skillsLoader.SetActive(active);
        if (!active)
        {
            CancelSkillWork();
            return;
        }

        _ = LoadComputerUseAsync();
    }

    public void RefreshWorkspace() => _skillsLoader.RefreshWorkspace();

    private void OnCustomizeLoaded(object sender, RoutedEventArgs e)
    {
        if (!_active) return;
        _skillsLoader.RefreshWorkspace();
        _renderedSkills = null;
        ApplySkillsState(_skillsLoader.State);
    }

    public void Dispose()
    {
        _active = false;
        _activationGeneration++;
        CancelSkillWork();
        _skillsLoader.Dispose();
        _skills?.Dispose();
        Loaded -= OnCustomizeLoaded;
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        var showPlugins = sender is ToggleButton { Tag: "plugins" };
        SkillsTab.IsChecked = !showPlugins;
        PluginsTab.IsChecked = showPlugins;
        SkillsPanel.Visibility = showPlugins ? Visibility.Collapsed : Visibility.Visible;
        PluginsPanel.Visibility = showPlugins ? Visibility.Visible : Visibility.Collapsed;
        // Search and Add act on skills only.
        var skillTools = showPlugins ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = skillTools;
        RefreshButton.Visibility = skillTools;
        AddButton.Visibility = skillTools;
        if (showPlugins) ShowStatus(null);
        else ApplySkillsState(_skillsLoader.State);
    }

    private void ShowStatus(string? text, string? details = null)
    {
        StatusText.Text = text ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(StatusText, details);
    }
}
