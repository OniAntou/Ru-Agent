using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuAgent.Client;
using RuAgent.Protocol.Subagents;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class SubagentTreeView
{
    private static readonly FontFamily DiffFont = new("Cascadia Code, Consolas");

    private async void OnReviewChanges(object sender, RoutedEventArgs e)
    {
        if (_disposed || _client is null || _model.Selected is not { } agent || _model.Result is not { } result)
        {
            return;
        }

        try
        {
            var changes = await _client.GetSubagentChangesAsync(
                _root, agent.AgentId, result.ResultVersion, _lifetime.Token);
            var content = new ScrollViewer
            {
                Content = BuildChangesPanel(changes),
                MaxHeight = 520,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            var dialog = new ContentDialog
            {
                Title = "Subagent changes",
                Content = content,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
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
    }

    private static StackPanel BuildChangesPanel(SubagentChangesResponse changes)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Read-only preview. Ask the main agent to apply this result.",
            FontSize = 12,
            Foreground = MutedBrush(),
            TextWrapping = TextWrapping.Wrap,
        });
        if (changes.Files.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No file changes were proposed.", FontSize = 13 });
            return panel;
        }

        var allPatches = changes.Files
            .Where(EligiblePatch)
            .Select(file => SubagentPatchFormatter.Format(file.Path, file.BaseText, file.ProposedText))
            .Where(patch => patch is not null)
            .ToArray();
        if (allPatches.Length > 0)
        {
            var copyAll = new Button
            {
                Content = "Copy all patches",
                Tag = string.Join("\n", allPatches),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(copyAll, "Copy all subagent patches");
            ToolTipService.SetToolTip(copyAll, "Copy unified patches for the user's own Git/PR flow. Ru never applies them.");
            copyAll.Click += OnCopyPatchClick;
            panel.Children.Add(copyAll);
        }

        foreach (var file in changes.Files)
        {
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(new TextBlock
            {
                Text = $"{file.Path} · {StatusLabel(file.Status)}",
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

            string? body = null;
            if (file.Binary)
            {
                body = "Binary file. Open it in the workspace to inspect the proposed bytes.";
            }
            else if (file.Truncated)
            {
                body = "Preview truncated by the review bound.";
            }
            else if (file.Status == "applied")
            {
                body = "Already integrated into the workspace.";
            }
            else if (file.Status == "deleted")
            {
                body = "The proposed result deletes this file.";
            }
            else if (file.Status == "diverged")
            {
                body = "The workspace file changed since the child captured its base; no clean diff is available.";
            }
            else if (file.ProposedText is { } proposed)
            {
                body = file.BaseText is { } @base && file.Status == "base"
                    ? UnifiedDiff(@base, proposed)
                    : proposed;
            }

            if (body is not null)
            {
                block.Children.Add(new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = body,
                        FontFamily = DiffFont,
                        FontSize = 11,
                        TextWrapping = TextWrapping.NoWrap,
                        IsTextSelectionEnabled = true,
                    },
                    MaxHeight = 260,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                });
            }

            if (EligiblePatch(file)
                && SubagentPatchFormatter.Format(file.Path, file.BaseText, file.ProposedText) is { } patch)
            {
                var copyPatch = new Button
                {
                    Content = "Copy patch",
                    Tag = patch,
                    Padding = new Thickness(8, 4, 8, 4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                AutomationProperties.SetName(copyPatch, "Copy unified patch for " + file.Path);
                ToolTipService.SetToolTip(copyPatch, "Copy a unified patch for git apply; Ru never applies it automatically.");
                copyPatch.Click += OnCopyPatchClick;
                block.Children.Add(copyPatch);
            }

            panel.Children.Add(block);
        }

        return panel;
    }

    private static bool EligiblePatch(SubagentChangedFileResponse file) =>
        file.Status is "base" or "created" or "deleted";

    private static void OnCopyPatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string patch } || patch.Length == 0) return;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(patch);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[Subagents] Patch copy failed: {error.GetType().Name}");
        }
    }

    private static string UnifiedDiff(string before, string after)
    {
        // DiffEngine is an LCS matrix; keep the preview bounded so a large file
        // cannot turn review into a UI freeze. Longer files fall back to "after".
        if (before.Length > 20_000 || after.Length > 20_000
            || CountLines(before) > 800 || CountLines(after) > 800)
        {
            return after;
        }

        var lines = DiffEngine.Compute(before, after);
        var builder = new StringBuilder();
        foreach (var line in lines.Take(500))
        {
            builder.Append(line.Kind switch
            {
                DiffKind.Added => "+ ",
                DiffKind.Removed => "- ",
                _ => "  ",
            });
            builder.AppendLine(line.Text);
        }

        if (lines.Count > 500)
        {
            builder.AppendLine("… diff truncated at 500 lines.");
        }

        return builder.ToString().TrimEnd();
    }

    private static int CountLines(string text) => text.Count(character => character == '\n') + 1;

    private static string StatusLabel(string status) => status switch
    {
        "base" => "diff vs captured base",
        "created" => "new file",
        "applied" => "already integrated",
        "deleted" => "deletion",
        "missing" => "file missing",
        "diverged" => "workspace changed",
        _ => status,
    };

    private static Brush MutedBrush() =>
        Application.Current?.Resources.TryGetValue("RuMutedBrush", out var value) == true && value is Brush brush
            ? brush
            : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184));
}
