using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuAgent.Client;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The user plugins on the Plugins tab (ADR 0044): one card per plugin with its parts and
/// an enable switch, and a New plugin dialog that scaffolds a local plugin through the Host.
/// The Host owns the list and the enabled set; this view shows it and sends each change.
/// </summary>
public sealed partial class CustomizeView
{
    private PluginsApiClient? _plugins;
    private string? _pluginsRoot;

    private async Task LoadPluginsAsync()
    {
        if (CreatePluginsClient() is not { } client) return;
        var generation = _activationGeneration;
        try
        {
            var list = await client.ListAsync();
            if (_active && generation == _activationGeneration) RenderPlugins(list);
        }
        catch (Exception error) when (error is AgentApiException or HttpRequestException or TaskCanceledException)
        {
            if (PluginsTab.IsChecked == true) ShowStatus("Could not load plugins.", error.Message);
        }
    }

    private async Task UpdatePluginsAsync(PluginSettingsRequest request)
    {
        if (CreatePluginsClient() is not { } client) return;
        try
        {
            RenderPlugins(await client.UpdateAsync(request));
            ShowStatus(null);
        }
        catch (Exception error) when (error is AgentApiException or HttpRequestException or TaskCanceledException)
        {
            ShowStatus(error is AgentApiException api ? api.Message : "Could not change the plugin.", error.Message);
            await LoadPluginsAsync();
        }
    }

    private PluginsApiClient? CreatePluginsClient()
    {
        if (_api is null) return null;
        // One client per bound Host: a reconnected Host is a new session.
        return _plugins ??= _api.CreatePluginsApiClient();
    }

    private void ResetPluginsClient()
    {
        _plugins?.Dispose();
        _plugins = null;
    }

    private void RenderPlugins(PluginListResponse list)
    {
        _pluginsRoot = list.LocalRoot;
        PluginsList.Children.Clear();
        foreach (var plugin in list.Plugins) PluginsList.Children.Add(PluginCard(plugin));
        PluginsEmpty.Visibility = list.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginsSkippedText.Text = list.Skipped.Count == 0 ? string.Empty : "Not loaded: " + string.Join(" · ", list.Skipped);
        PluginsSkippedText.Visibility = list.Skipped.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement PluginCard(PluginItem plugin)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(Text(plugin.Name, 13.5, "RuInkBrush", bold: true));
        if (!string.IsNullOrWhiteSpace(plugin.Version)) title.Children.Add(Text("v" + plugin.Version, 11.5, "RuMutedBrush"));

        var details = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(title);
        if (!string.IsNullOrWhiteSpace(plugin.Description))
            details.Children.Add(Text(plugin.Description, 12.5, "RuMutedBrush", wrap: true));

        var parts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        if (plugin.HasSkills) parts.Children.Add(Chip("Skills", "RuInkBrush"));
        if (plugin.HasInstructions) parts.Children.Add(Chip("Instructions", "RuInkBrush"));
        if (plugin.HasHooks) parts.Children.Add(Chip("Hooks · not loaded yet", "RuMutedBrush"));
        if (plugin.HasMcpServers) parts.Children.Add(Chip("MCP · not loaded yet", "RuMutedBrush"));
        parts.Children.Add(Chip(plugin.Source == "managed" ? "Installed" : "Local", "RuMutedBrush"));
        details.Children.Add(parts);
        foreach (var note in plugin.Diagnostics.Where(note => !note.StartsWith("Hooks and MCP", StringComparison.Ordinal)))
            details.Children.Add(Text(note, 12, "RuWarningBrush", wrap: true));

        var toggle = new ToggleSwitch
        {
            IsOn = plugin.Enabled,
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = plugin.Id,
        };
        AutomationProperties.SetName(toggle, $"Use the {plugin.Name} plugin");
        toggle.Toggled += OnPluginToggled;

        var open = new Button
        {
            Style = (Style)Application.Current.Resources["RuToolbarButtonStyle"],
            Padding = new Thickness(8, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = plugin.Directory,
            Content = new FontIcon { Glyph = "", FontSize = 13 },
        };
        ToolTipService.SetToolTip(open, "Open the plugin's folder");
        AutomationProperties.SetName(open, $"Open the {plugin.Name} plugin folder");
        open.Click += OnOpenPluginFolderClick;

        var icon = new Border
        {
            Width = 32,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = (CornerRadius)Application.Current.Resources["AppControlCornerRadius"],
            Background = Brush("RuSurfaceBrush"),
            Child = new FontIcon { Glyph = "", FontSize = 14, Foreground = Brush("RuMutedBrush") },
        };

        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(details, 1);
        Grid.SetColumn(open, 2);
        Grid.SetColumn(toggle, 3);
        row.Children.Add(icon);
        row.Children.Add(details);
        row.Children.Add(open);
        row.Children.Add(toggle);
        return new Border { Style = (Style)Application.Current.Resources["FluentCardStyle"], Padding = new Thickness(16, 12, 16, 12), Child = row };
    }

    private static TextBlock Text(string text, double size, string brush, bool bold = false, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = Brush(brush),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static Border Chip(string text, string brush) => new()
    {
        Padding = new Thickness(8, 2, 8, 2),
        CornerRadius = (CornerRadius)Application.Current.Resources["AppControlCornerRadius"],
        Background = Brush("RuSurfaceBrush"),
        Child = Text(text, 11.5, brush),
    };

    private async void OnPluginToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: string id } toggle) return;
        toggle.IsEnabled = false;
        await UpdatePluginsAsync(new PluginSettingsRequest("set_enabled", id, Enabled: toggle.IsOn));
    }

    private void OnOpenPluginFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string directory }) OpenFolder(directory);
    }

    private void OnOpenPluginsFolderClick(object sender, RoutedEventArgs e)
    {
        if (_pluginsRoot is null) return;
        try { Directory.CreateDirectory(_pluginsRoot); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        OpenFolder(_pluginsRoot);
    }

    private static void OpenFolder(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Customize] Open folder failed: {error.GetType().Name}");
        }
    }

    private async void OnNewPluginClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = new TextBox { Header = "Name", PlaceholderText = "My team rules", MaxLength = 64 };
            var id = new TextBox { Header = "Id", PlaceholderText = "my-team-rules", MaxLength = 24 };
            var description = new TextBox { Header = "Description (optional)", MaxLength = 512, TextWrapping = TextWrapping.Wrap };
            var instructions = new CheckBox { Content = "Instructions (AGENTS.md)", IsChecked = true };
            var skill = new CheckBox { Content = "An example skill", IsChecked = true };
            var error = Text(string.Empty, 12, "RuDangerBrush", wrap: true);
            bool idEdited = false;
            name.TextChanged += (_, _) => { if (!idEdited) id.Text = SuggestPluginId(name.Text); };
            id.TextChanged += (_, _) => idEdited = id.FocusState != FocusState.Unfocused;

            var form = new StackPanel { Spacing = 12, MinWidth = 380 };
            form.Children.Add(name);
            form.Children.Add(id);
            form.Children.Add(description);
            form.Children.Add(Text("Start with", 12.5, "RuMutedBrush"));
            form.Children.Add(instructions);
            form.Children.Add(skill);
            form.Children.Add(error);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "New plugin",
                Content = form,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(name.Text)) { error.Text = "Give the plugin a name."; args.Cancel = true; }
                else if (!RuAgent.Protocol.UserPlugins.IsValidId(id.Text))
                { error.Text = "The id is 1 to 24 lowercase letters, digits or dashes, starting with a letter."; args.Cancel = true; }
            };
            if (await ContentDialogGate.ShowAsync(dialog) != ContentDialogResult.Primary) return;

            await UpdatePluginsAsync(new PluginSettingsRequest("create", id.Text, Name: name.Text.Trim(),
                Description: description.Text, WithInstructions: instructions.IsChecked == true, WithSkill: skill.IsChecked == true));
            if (_pluginsRoot is not null) OpenFolder(Path.Combine(_pluginsRoot, id.Text));
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Customize] New plugin failed: {error.GetType().Name}");
        }
    }

    /// <summary>A valid plugin id from a display name: lowercase ASCII, dashes, at most 24 characters.</summary>
    internal static string SuggestPluginId(string name)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var character in name.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD))
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(character);
            else if (character == 'đ') builder.Append('d');
            else if (char.IsWhiteSpace(character) || character is '-' or '_' or '.')
            {
                if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
            }
        }

        var id = builder.ToString().Trim('-');
        while (id.Length > 0 && !char.IsAsciiLetter(id[0])) id = id[1..];
        if (id.Length > 24) id = id[..24].TrimEnd('-');
        return id;
    }
}
