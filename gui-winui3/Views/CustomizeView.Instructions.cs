using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;
using Windows.System;

namespace RuApp.Views;

/// <summary>
/// The Instructions tab: one card per instruction file, for every project and for the
/// current workspace. Discard and Save act only on unsaved changes, and saving never
/// overwrites an edit made outside Ru since the file was loaded without asking first.
/// </summary>
public sealed partial class CustomizeView
{
    private InstructionFileSnapshot? _globalInstructions;
    private InstructionFileSnapshot? _workspaceInstructions;
    private bool _loadingInstructions;
    private readonly Dictionary<string, string> _instructionsNotice = new(StringComparer.Ordinal);

    private void LoadInstructions()
    {
        _loadingInstructions = true;
        try
        {
            _globalInstructions = ReadInstructions(InstructionFileStore.GlobalPath());
            GlobalInstructionsPath.Text = @"~\.ru\AGENTS.md · used in every project";
            ToolTipService.SetToolTip(GlobalInstructionsPath, _globalInstructions?.Path);
            GlobalInstructionsBox.Text = _globalInstructions?.Text ?? string.Empty;

            var workspace = _workspace();
            var workspacePath = InstructionFileStore.WorkspacePath(workspace);
            _workspaceInstructions = workspacePath is null ? null : ReadInstructions(workspacePath);
            bool hasWorkspace = workspacePath is not null;
            WorkspaceInstructionsPath.Text = hasWorkspace
                ? $@"{Path.GetFileName(workspace!.TrimEnd('\\', '/'))}\.ru\AGENTS.md · this project only"
                : "No workspace open";
            ToolTipService.SetToolTip(WorkspaceInstructionsPath, workspacePath);
            WorkspaceInstructionsBox.Text = _workspaceInstructions?.Text ?? string.Empty;
            var editor = hasWorkspace ? Visibility.Visible : Visibility.Collapsed;
            WorkspaceInstructionsBox.Visibility = WorkspaceInstructionsBoxFooter.Visibility = WorkspaceInstructionsBadge.Visibility = editor;
            WorkspaceInstructionsEmpty.Visibility = hasWorkspace ? Visibility.Collapsed : Visibility.Visible;

            RenderOtherSources(InstructionFileStore.OtherWorkspaceSources(workspace));
        }
        finally
        {
            _loadingInstructions = false;
        }

        _instructionsNotice.Clear();
        UpdateInstructionsState("global");
        UpdateInstructionsState("workspace");
    }

    /// <summary>The project's shared files, as small chips with a dot for present or missing.</summary>
    private void RenderOtherSources(IReadOnlyList<(string Name, bool Exists)> sources)
    {
        WorkspaceOtherSources.Children.Clear();
        foreach (var (name, exists) in sources)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            chip.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Brush(exists ? "RuSuccessBrush" : "RuLineBrush"),
            });
            chip.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 12,
                FontFamily = (FontFamily)Application.Current.Resources["RuMonoFontFamily"],
                Foreground = Brush(exists ? "RuInkBrush" : "RuMutedBrush"),
            });
            var border = new Border
            {
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = (CornerRadius)Application.Current.Resources["AppControlCornerRadius"],
                Background = Brush("RuSurfaceBrush"),
                Child = chip,
            };
            ToolTipService.SetToolTip(border, exists ? $"{name} is read too; edit it in the project." : $"No {name} in this project.");
            WorkspaceOtherSources.Children.Add(border);
        }

        WorkspaceOtherSourcesPanel.Visibility = sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static InstructionFileSnapshot? ReadInstructions(string path)
    {
        try
        {
            return InstructionFileStore.Read(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Instructions] Read failed: {error.GetType().Name}");
            return null;
        }
    }

    private void OnInstructionsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingInstructions || sender is not FrameworkElement { Tag: string scope }) return;
        _instructionsNotice.Remove(scope);
        UpdateInstructionsState(scope);
    }

    private void OnInstructionsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (control && e.Key == VirtualKey.S && sender is FrameworkElement { Tag: string scope })
        {
            e.Handled = true;
            if (InstructionsParts(scope).Save.IsEnabled) SaveInstructions(scope);
        }
    }

    private void OnInstructionsRevertClick(object sender, RoutedEventArgs e) => LoadInstructions();

    private void OnInstructionsSaveClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string scope }) SaveInstructions(scope);
    }

    private async void SaveInstructions(string scope)
    {
        try
        {
            var parts = InstructionsParts(scope);
            if (parts.Snapshot is null) return;
            var outcome = InstructionFileStore.Save(parts.Snapshot, parts.Box.Text);
            if (outcome == InstructionSaveOutcome.ChangedOnDisk && await ConfirmOverwriteAsync(parts.Snapshot.Path))
                outcome = InstructionFileStore.Save(parts.Snapshot, parts.Box.Text, overwrite: true);

            if (outcome == InstructionSaveOutcome.Saved)
            {
                LoadInstructions();
                _instructionsNotice[scope] = "Saved · applies from your next message";
                UpdateInstructionsState(scope);
                return;
            }

            _instructionsNotice[scope] = outcome switch
            {
                InstructionSaveOutcome.ChangedOnDisk => "Not saved: the file changed outside Ru. Discard to load it first.",
                InstructionSaveOutcome.TooLarge => $"Not saved: Ru reads at most {InstructionFileStore.MaxBytes / 1024} KB per file.",
                InstructionSaveOutcome.Unsafe => "Not saved: the file or its folder is a link, which Ru does not follow.",
                _ => "Could not save the file.",
            };
            UpdateInstructionsState(scope);
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Instructions] Save failed: {error.GetType().Name}");
        }
    }

    private async Task<bool> ConfirmOverwriteAsync(string path)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "File changed outside Ru",
            Content = $"{path} was edited since you opened it here. Overwrite it with your text?",
            PrimaryButtonText = "Overwrite",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await ContentDialogGate.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>Badge, counter and buttons for one card, from its text and the loaded file.</summary>
    private void UpdateInstructionsState(string scope)
    {
        var parts = InstructionsParts(scope);
        int bytes = InstructionFileStore.ByteCount(parts.Box.Text);
        bool readable = parts.Snapshot is not null;
        bool changed = readable
            && InstructionFileStore.NormalizeNewlines(parts.Box.Text) != InstructionFileStore.NormalizeNewlines(parts.Snapshot!.Text);
        bool tooLarge = bytes > InstructionFileStore.MaxBytes;

        parts.Save.IsEnabled = readable && changed && !tooLarge;
        parts.Discard.IsEnabled = readable && changed;
        parts.BadgeText.Text = !readable ? "Unreadable"
            : changed ? "Unsaved"
            : parts.Snapshot!.Exists ? "Saved" : "Not created";
        parts.BadgeText.Foreground = Brush(changed ? "RuWarningBrush" : "RuMutedBrush");

        string size = bytes == 0 ? "Empty" : $"{bytes / 1024.0:0.#} KB of {InstructionFileStore.MaxBytes / 1024} KB";
        bool hasNotice = _instructionsNotice.TryGetValue(scope, out var notice);
        parts.Status.Text = hasNotice ? notice!
            : !readable ? "This file could not be read."
            : tooLarge ? $"{size} · too long, Ru would skip this file"
            : size;
        bool problem = tooLarge || (hasNotice && notice!.StartsWith("Not saved", StringComparison.Ordinal));
        parts.Status.Foreground = Brush(problem ? "RuDangerBrush" : "RuMutedBrush");
    }

    private (InstructionFileSnapshot? Snapshot, TextBox Box, TextBlock Status, TextBlock BadgeText, Button Save, Button Discard)
        InstructionsParts(string scope) =>
        scope == "workspace"
            ? (_workspaceInstructions, WorkspaceInstructionsBox, WorkspaceInstructionsStatus, WorkspaceInstructionsBadgeText,
                WorkspaceInstructionsSave, WorkspaceInstructionsRevert)
            : (_globalInstructions, GlobalInstructionsBox, GlobalInstructionsStatus, GlobalInstructionsBadgeText,
                GlobalInstructionsSave, GlobalInstructionsRevert);
}
