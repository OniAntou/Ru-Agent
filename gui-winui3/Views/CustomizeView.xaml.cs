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
public sealed partial class CustomizeView : UserControl
{
    private ApiService? _api;
    private Func<string?> _workspace = () => null;
    private bool _active;

    public CustomizeView()
    {
        InitializeComponent();
    }

    /// <summary>A skill the user chose to run; the shell returns to chat with it typed.</summary>
    public event EventHandler<string>? UseSkillRequested;

    public void Initialize(ApiService apiService, Func<string?> workspace)
    {
        _api = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        // A reconnected Host is a new session: the old skills client is bound to the last one.
        _skills = null;
    }

    /// <summary>Loads fresh state each time the page is shown; hidden, it cancels its work.</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (!active)
        {
            CancelSkillWork();
            return;
        }

        _ = LoadSkillsAsync();
        _ = LoadComputerUseAsync();
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
        ShowStatus(null);
    }

    private void ShowStatus(string? text, string? details = null)
    {
        StatusText.Text = text ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(StatusText, details);
    }
}
