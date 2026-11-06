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
        _pluginsById.Clear();
        foreach (var plugin in list.Plugins) _pluginsById[plugin.Id] = plugin;
        PluginsList.Children.Clear();
        foreach (var plugin in list.Plugins) PluginsList.Children.Add(PluginCard(plugin));
        PluginsEmpty.Visibility = list.Plugins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PluginsSkippedText.Text = list.Skipped.Count == 0 ? string.Empty : "Not loaded: " + string.Join(" · ", list.Skipped);
        PluginsSkippedText.Visibility = list.Skipped.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Opens chat with the built-in create-plugin skill in the composer.</summary>
    private void OnAskRuForPluginClick(object sender, RoutedEventArgs e) =>
        UseSkillRequested?.Invoke(this, "create-plugin");

    private void OnPluginsPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width - 16;
        FlowIntoColumns(PluginStartCards, width, minimumColumnWidth: 240);
        FlowIntoColumns(PluginPartCards, width, minimumColumnWidth: 200);
    }

    /// <summary>
    /// Lays a grid's children out in equal columns, as many as fit without leaving a short last
    /// row, so the cards fill a wide window and stack on a narrow one.
    /// </summary>
    private static void FlowIntoColumns(Grid grid, double width, double minimumColumnWidth)
    {
        var count = grid.Children.Count;
        if (count == 0 || width <= 0) return;
        var columns = (int)Math.Clamp(Math.Floor((width + grid.ColumnSpacing) / (minimumColumnWidth + grid.ColumnSpacing)), 1, count);
        // Prefer even rows (3 → 3 or 1, 4 → 4 or 2) over a lone card on the last row.
        while (columns > 1 && count % columns != 0) columns--;
        var rows = (count + columns - 1) / columns;
        if (grid.ColumnDefinitions.Count == columns && grid.RowDefinitions.Count == rows) return;
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < rows; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < count; index++)
        {
            Grid.SetColumn((FrameworkElement)grid.Children[index], index % columns);
            Grid.SetRow((FrameworkElement)grid.Children[index], index / columns);
        }
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
        if (plugin.HasHooks || plugin.HasMcpServers)
        {
            var runs = plugin.HasHooks && plugin.HasMcpServers ? "Hooks and MCP" : plugin.HasHooks ? "Hooks" : "MCP";
            parts.Children.Add(plugin.Consent switch
            {
                "approved" => Chip($"{runs} · approved", "RuInkBrush"),
                "changed" => Chip($"{runs} · changed, review again", "RuWarningBrush"),
                _ => Chip($"{runs} · needs approval", "RuWarningBrush"),
            });
        }

        parts.Children.Add(Chip(plugin.Source == "managed" ? "Installed" : "Local", "RuMutedBrush"));
        details.Children.Add(parts);
        foreach (var note in plugin.Diagnostics)
            details.Children.Add(Text(note, 12, "RuWarningBrush", wrap: true));
        if (plugin.Consent != "not_needed")
        {
            var review = new HyperlinkButton
            {
                Content = plugin.NeedsConsent ? "Review commands" : "View commands",
                Padding = new Thickness(0),
                Tag = plugin,
            };
            AutomationProperties.SetName(review, $"Review the commands of the {plugin.Name} plugin");
            review.Click += OnReviewPluginCommandsClick;
            details.Children.Add(review);
        }

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

    private readonly Dictionary<string, PluginItem> _pluginsById = new(StringComparer.Ordinal);

    private async void OnPluginToggled(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not ToggleSwitch { Tag: string id } toggle) return;
            toggle.IsEnabled = false;
            // Turning on a plugin whose commands wait on consent asks first, showing exactly them.
            if (toggle.IsOn && _pluginsById.TryGetValue(id, out var plugin) && plugin.NeedsConsent)
            {
                var choice = await AskPluginConsentAsync(plugin, turningOn: true);
                if (choice == PluginConsentChoice.Cancel)
                {
                    await LoadPluginsAsync();
                    return;
                }

                if (choice == PluginConsentChoice.Allow)
                    await UpdatePluginsAsync(new PluginSettingsRequest("approve", id, Digest: plugin.Digest));
            }

            await UpdatePluginsAsync(new PluginSettingsRequest("set_enabled", id, Enabled: toggle.IsOn));
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Customize] Plugin toggle failed: {error.GetType().Name}");
        }
    }

    private async void OnReviewPluginCommandsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement { Tag: PluginItem plugin }) return;
            var choice = await AskPluginConsentAsync(plugin, turningOn: false);
            if (choice == PluginConsentChoice.Allow)
                await UpdatePluginsAsync(new PluginSettingsRequest("approve", plugin.Id, Digest: plugin.Digest));
            else if (choice == PluginConsentChoice.Revoke)
                await UpdatePluginsAsync(new PluginSettingsRequest("revoke", plugin.Id));
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Customize] Plugin review failed: {error.GetType().Name}");
        }
    }

    private enum PluginConsentChoice { Cancel, Allow, WithoutCommands, Revoke }

    /// <summary>
    /// Shows every command the plugin would run, as the Host read them, and asks for consent.
    /// The approval sent back carries the digest of exactly this list.
    /// </summary>
    private async Task<PluginConsentChoice> AskPluginConsentAsync(PluginItem plugin, bool turningOn)
    {
        var body = new StackPanel { Spacing = 10, MaxWidth = 560 };
        body.Children.Add(Text(plugin.Consent == "changed"
            ? "This plugin's commands changed since you approved them, so they are paused. Ru will run these as you, without asking again:"
            : "When this plugin is on, Ru will run these commands as you, without asking again:", 13, "RuInkBrush", wrap: true));
        var list = new StackPanel { Spacing = 6 };
        foreach (var line in plugin.Executable ?? [])
        {
            var text = Text(line, 12, "RuInkBrush", wrap: true);
            text.FontFamily = (FontFamily)Application.Current.Resources["RuMonoFontFamily"];
            text.IsTextSelectionEnabled = true;
            list.Children.Add(new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = (CornerRadius)Application.Current.Resources["AppControlCornerRadius"],
                Background = Brush("RuSurfaceBrush"),
                Child = text,
            });
        }

        body.Children.Add(new ScrollViewer { MaxHeight = 280, Content = list });
        body.Children.Add(Text("Only allow commands from a plugin you trust. If the plugin changes them later, they stop until you approve again. Tool calls still follow your permission preset.",
            12, "RuMutedBrush", wrap: true));

        var approved = plugin.Consent == "approved";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Allow commands from {plugin.Name}?",
            Content = body,
            PrimaryButtonText = approved ? string.Empty : turningOn ? "Allow and turn on" : "Allow",
            SecondaryButtonText = turningOn ? "Turn on without commands" : approved ? "Stop allowing" : string.Empty,
            CloseButtonText = turningOn ? "Cancel" : "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        return await ContentDialogGate.ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => PluginConsentChoice.Allow,
            ContentDialogResult.Secondary => turningOn ? PluginConsentChoice.WithoutCommands : PluginConsentChoice.Revoke,
            _ => PluginConsentChoice.Cancel,
        };
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

    private async void OnInstallPluginPackageClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool update = sender is FrameworkElement { Tag: "update" };
            var url = new TextBox
            {
                Header = "GitHub folder URL, pinned to a commit",
                PlaceholderText = "https://github.com/owner/repo/tree/<40-character commit>/plugins/my-plugin",
                MinWidth = 460,
            };
            var form = new StackPanel { Spacing = 12 };
            form.Children.Add(url);
            form.Children.Add(Text(update
                ? "Replaces a plugin Ru installed from the same repository and folder, if you have not changed its files. If its commands changed, they stay paused until you allow them again."
                : "Ru downloads only that folder at that exact commit and runs nothing while installing. The plugin stays off until you turn it on, and any commands it has wait for your approval.",
                12, "RuMutedBrush", wrap: true));
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = update ? "Update a plugin from GitHub" : "Install a plugin from GitHub",
                Content = form,
                PrimaryButtonText = update ? "Update" : "Install",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await ContentDialogGate.ShowAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(url.Text)) return;
            await InstallPluginPackageAsync(url.Text.Trim(), update);
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Customize] Plugin install failed: {error.GetType().Name}");
        }
    }

    /// <summary>
    /// Copies a plugin draft (for example one Ru wrote with the create-plugin skill) into your
    /// plugins. It is added switched off; turning it on asks before any of its commands run.
    /// </summary>
    private async void OnImportPluginClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await AskAsync("Add a plugin from a folder", "Full path of a folder with ru-plugin.json", "Add");
            if (folder is not null) await ImportPluginAsync(folder);
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Customize] Plugin import failed: {error.GetType().Name}");
        }
    }

    private async Task ImportPluginAsync(string folder)
    {
        if (CreatePluginsClient() is not { } client) return;
        ShowStatus("Adding the plugin…");
        try
        {
            RenderPlugins(await client.ImportAsync(folder));
            ShowStatus("Added the plugin. Turn it on when you are ready.");
        }
        catch (Exception error) when (error is AgentApiException or HttpRequestException or TaskCanceledException)
        {
            ShowStatus(error is AgentApiException api ? api.Message : "Could not add the plugin.", error.Message);
        }
    }

    private async Task InstallPluginPackageAsync(string sourceUrl, bool update)
    {
        if (CreatePluginsClient() is not { } client) return;
        ShowStatus(update ? "Updating the plugin…" : "Installing the plugin…");
        try
        {
            var result = await client.InstallPackageAsync(sourceUrl, update);
            RenderPlugins(result);
            var receipt = result.Receipt!;
            ShowStatus($"{(receipt.Action == "updated" ? "Updated" : "Installed")} {receipt.Name} at commit {receipt.Commit[..7]}. Turn it on when you are ready.");
        }
        catch (Exception error) when (error is AgentApiException or HttpRequestException or TaskCanceledException)
        {
            ShowStatus(error is AgentApiException api ? api.Message : "Could not install the plugin.", error.Message);
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
