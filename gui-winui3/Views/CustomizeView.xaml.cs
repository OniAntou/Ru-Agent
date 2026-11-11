using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The Customize page: the workspace's skills, Ru's plugins and the user's own
/// instruction files, opened from the sidebar in place of the chat. The Host owns
/// the skill and plugin settings; the page reads them on activation and writes each
/// change back through the API. Instruction files are the user's own Markdown files,
/// which the page edits directly (<see cref="InstructionFileStore"/>) and the Host
/// reads as guidance when it prepares each turn.
/// </summary>
public sealed partial class CustomizeView : UserControl, IDisposable
{
    private ApiService? _api;
    private Func<string?> _workspace = () => null;
    private Func<bool> _workspaceIsScratch = () => false;
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

    public void Initialize(ApiService apiService, Func<string?> workspace, Func<bool>? workspaceIsScratch = null)
    {
        CancelSkillWork();
        _activationGeneration++;
        _api = apiService ?? throw new ArgumentNullException(nameof(apiService));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _workspaceIsScratch = workspaceIsScratch ?? (() => false);
        // A reconnected Host is a new session: the old skills client is bound to the last one.
        _skills?.Dispose();
        ResetPluginsClient();
        var skills = _skills = _api.CreateSkillsApiClient();
        _skillsLoader.Attach(skills.ListAsync, _workspace);
        if (_active)
        {
            _ = LoadComputerUseAsync();
            _ = LoadPluginsAsync();
        }
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
        _ = LoadPluginsAsync();
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
        ResetPluginsClient();
        Loaded -= OnCustomizeLoaded;
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        var tab = sender is ToggleButton { Tag: string tag } ? tag : "skills";
        SkillsTab.IsChecked = tab == "skills";
        PluginsTab.IsChecked = tab == "plugins";
        InstructionsTab.IsChecked = tab == "instructions";
        SkillsPanel.Visibility = tab == "skills" ? Visibility.Visible : Visibility.Collapsed;
        PluginsPanel.Visibility = tab == "plugins" ? Visibility.Visible : Visibility.Collapsed;
        InstructionsPanel.Visibility = tab == "instructions" ? Visibility.Visible : Visibility.Collapsed;
        // Search and Add act on skills only.
        var skillTools = tab == "skills" ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = skillTools;
        RefreshButton.Visibility = skillTools;
        AddButton.Visibility = skillTools;
        if (tab == "skills") ApplySkillsState(_skillsLoader.State);
        else ShowStatus(null);
        if (tab == "plugins") WorkspaceText.Text = "Optional abilities you can turn on for new turns.";
        if (tab == "instructions") WorkspaceText.Text = "Your guidance for Ru, read before every turn.";
        if (tab == "instructions") LoadInstructions();
    }

    private void ShowStatus(string? text, string? details = null)
    {
        StatusText.Text = text ?? string.Empty;
        StatusText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(StatusText, details);
    }
}
