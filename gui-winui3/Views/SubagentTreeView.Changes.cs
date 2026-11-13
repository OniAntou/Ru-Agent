using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using RuAgent.Client;
using RuAgent.Protocol.Subagents;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The read-only change review: per-file hunks with added/removed colouring and
/// copyable patches. Nothing here applies, approves or writes; the main agent stays
/// the only actor that can integrate a result.
/// </summary>
public sealed partial class SubagentTreeView
{
    internal const int MaxRenderedDiffLines = 400;
    private static readonly FontFamily DiffFont = new("Cascadia Code, Consolas");
    private bool _reviewing;

    private async void OnReviewChanges(object sender, RoutedEventArgs e)
    {
        if (_disposed || _reviewing || _client is null || _model.Selected is not { } agent || _model.Result is not { } result)
        {
            return;
        }

        _reviewing = true;
        ReviewChangesButton.IsEnabled = false;
        ReviewChangesLabel.Text = "Loading changes…";
        try
        {
            var changes = await _client.GetSubagentChangesAsync(
                _root, agent.AgentId, result.ResultVersion, _lifetime.Token);
            ShowFeedback("");
            // The diff is CPU work bounded by the Host's review caps; keep it off the UI thread.
            var files = await Task.Run(() => changes.Files.Select(SubagentChangeReview.File).ToArray(), _lifetime.Token);
            var width = Math.Clamp(XamlRoot.Size.Width - 160, 360, 860);
            var dialog = new ContentDialog
            {
                Title = "Subagent changes",
                Content = new ScrollViewer
                {
                    Content = BuildChangesPanel(files),
                    Width = width,
                    MaxHeight = 560,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            // The stock dialog caps at 548px, too narrow for a diff.
            dialog.Resources["ContentDialogMaxWidth"] = width + 80;
            await ContentDialogGate.ShowAsync(dialog);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            ShowFeedback(error is AgentApiException api && api.ErrorCode == "subagent_changes_not_found"
                ? "This result has no file changes to review."
                : "Couldn't load the change preview. Try again.");
            GuiDiagnosticWriter.AppendTraceLine($"[Subagents] Changes read failed: {error.GetType().Name}");
        }
        finally
        {
            _reviewing = false;
            if (!_disposed)
            {
                ReviewChangesButton.IsEnabled = true;
                ReviewChangesLabel.Text = "Review changes";
            }
        }
    }

    private static StackPanel BuildChangesPanel(IReadOnlyList<SubagentReviewFile> files)
    {
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = "Read-only preview. Ask the main agent to apply this result.",
            FontSize = 12,
            Foreground = Brush("RuMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (files.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No file changes were proposed.", FontSize = 13 });
            return panel;
        }

        var summary = new Grid { ColumnSpacing = 12 };
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        summary.Children.Add(new TextBlock
        {
            Text = SubagentChangeReview.Summary(files),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var patches = files.Select(file => file.Patch).OfType<string>().ToArray();
        if (patches.Length > 0)
        {
            var copyAll = CopyButton(patches.Length == 1 ? "Copy patch" : "Copy all patches", string.Concat(patches),
                "Copy all subagent patches", "Copy unified patches for your own Git/PR flow. Ru never applies them.");
            Grid.SetColumn(copyAll, 1);
            summary.Children.Add(copyAll);
        }

        panel.Children.Add(summary);
        foreach (var file in files) panel.Children.Add(BuildFileBlock(file, showCopy: patches.Length > 1));
        return panel;
    }

    private static StackPanel BuildFileBlock(SubagentReviewFile file, bool showCopy)
    {
        var block = new StackPanel { Spacing = 6 };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        title.Inlines.Add(new Run { Text = file.Path, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        title.Inlines.Add(new Run { Text = "  " + file.StatusLabel, Foreground = Brush("RuMutedBrush") });
        if (file.Diff is not null)
        {
            title.Inlines.Add(new Run { Text = $"  +{file.Added}", Foreground = Brush("RuSuccessBrush") });
            title.Inlines.Add(new Run { Text = $" −{file.Removed}", Foreground = Brush("RuDangerBrush") });
        }

        header.Children.Add(title);
        if (showCopy && file.Patch is { } patch)
        {
            var copy = CopyButton("Copy patch", patch, "Copy unified patch for " + file.Path,
                "Copy a unified patch for git apply. Ru never applies it automatically.");
            Grid.SetColumn(copy, 1);
            header.Children.Add(copy);
        }

        block.Children.Add(header);
        if (file.Note is { } note)
        {
            block.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = Brush("RuMutedBrush"), TextWrapping = TextWrapping.Wrap });
        }

        var body = file.Diff is { } diff ? DiffBody(diff) : file.Fallback is { } text ? PlainBody(text) : null;
        if (body is not null)
        {
            block.Children.Add(new Border
            {
                BorderBrush = Brush("RuLineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = UiCornerRadius.Control,
                Padding = new Thickness(10, 8, 10, 8),
                Child = new ScrollViewer
                {
                    Content = body,
                    MaxHeight = 320,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            });
        }

        return block;
    }

    private static RichTextBlock DiffBody(SubagentFileDiff diff)
    {
        var paragraph = new Paragraph();
        int rendered = 0;
        int total = diff.Hunks.Sum(hunk => hunk.Lines.Count);
        foreach (var hunk in diff.Hunks)
        {
            if (rendered >= MaxRenderedDiffLines) break;
            AddLine(paragraph, $"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@", "RuInfoBrush");
            foreach (var line in hunk.Lines)
            {
                if (rendered++ >= MaxRenderedDiffLines) break;
                var (prefix, brush) = line.Op switch
                {
                    SubagentDiffOp.Added => ("+ ", "RuSuccessBrush"),
                    SubagentDiffOp.Removed => ("- ", "RuDangerBrush"),
                    _ => ("  ", "RuInkBrush"),
                };
                AddLine(paragraph, prefix + line.Text.TrimEnd('\r'), brush);
            }
        }

        if (total > MaxRenderedDiffLines)
        {
            AddLine(paragraph, $"… {total - MaxRenderedDiffLines} more diff lines. Copy the patch to see all of them.", "RuMutedBrush");
        }

        return Mono(paragraph);
    }

    private static RichTextBlock PlainBody(string text)
    {
        var paragraph = new Paragraph();
        var lines = text.Split('\n');
        foreach (var line in lines.Take(MaxRenderedDiffLines)) AddLine(paragraph, line.TrimEnd('\r'), "RuInkBrush");
        if (lines.Length > MaxRenderedDiffLines)
        {
            AddLine(paragraph, $"… {lines.Length - MaxRenderedDiffLines} more lines.", "RuMutedBrush");
        }

        return Mono(paragraph);
    }

    private static void AddLine(Paragraph paragraph, string text, string brushKey)
    {
        if (paragraph.Inlines.Count > 0) paragraph.Inlines.Add(new LineBreak());
        // An empty Run collapses its line; keep the row so line positions stay honest.
        var run = new Run { Text = text.Length == 0 ? " " : text };
        if (Brush(brushKey) is { } brush) run.Foreground = brush;
        paragraph.Inlines.Add(run);
    }

    private static RichTextBlock Mono(Paragraph paragraph)
    {
        var view = new RichTextBlock
        {
            FontFamily = DiffFont,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };
        view.Blocks.Add(paragraph);
        return view;
    }

    private static Button CopyButton(string label, string patch, string automationName, string toolTip)
    {
        var button = new Button { Content = label, Padding = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, automationName);
        ToolTipService.SetToolTip(button, toolTip);
        button.Click += async (_, _) =>
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(patch);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                button.Content = "Copied";
            }
            catch (Exception error)
            {
                button.Content = "Couldn't copy";
                GuiDiagnosticWriter.AppendTraceLine($"[Subagents] Patch copy failed: {error.GetType().Name}");
            }

            // Confirmation is transient; the button stays usable for a second copy.
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            button.Content = label;
        };
        return button;
    }
}
