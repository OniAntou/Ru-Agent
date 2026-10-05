using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class ChatView
{
    private static readonly TimeSpan WorkStateHandoffTimeout = TimeSpan.FromSeconds(20);
    private bool _isChoosingWorkStateHandoff;

    private async void OnHandoffWorkStateClick(object sender, RoutedEventArgs e)
    {
        if (_isChoosingWorkStateHandoff)
        {
            return;
        }
        _isChoosingWorkStateHandoff = true;
        HandoffWorkStateButton.IsEnabled = false;
        var target = CaptureHandoffTarget();
        var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(WorkStateHandoffTimeout);
        try
        {
            if (_apiService is null || string.IsNullOrWhiteSpace(_activeSessionId))
            {
                ShowAgentInlineStatus("Saved work context is unavailable until a chat session is active.");
                return;
            }

            var sessions = await _apiService.GetSessionsAsync(cancellation.Token);
            if (!IsHandoffTargetCurrent(target))
            {
                return;
            }
            if (sessions is null || sessions.Sessions.Count == 0)
            {
                ShowAgentInlineStatus("Could not list saved sessions for a work-context handoff.");
                return;
            }

            var sourceSession = await SelectHandoffSessionAsync(sessions.Sessions);
            if (sourceSession is null || !IsHandoffTargetCurrent(target))
            {
                return;
            }

            var runs = await _apiService.ListSessionRunIdsAsync(sourceSession.Id, cancellation.Token);
            if (!IsHandoffTargetCurrent(target))
            {
                return;
            }
            if (runs is null || runs.Count == 0)
            {
                ShowAgentInlineStatus("The selected session has no readable saved runs.");
                return;
            }

            var selectedRunId = await SelectHandoffRunAsync(runs);
            if (selectedRunId is null || !IsHandoffTargetCurrent(target))
            {
                return;
            }

            var preparation = await WorkStateHandoffCoordinator.PrepareSelectedRunAsync(
                selectedRunId,
                sourceSession.Id,
                target.WorkspaceId,
                (runId, token) => _apiService.GetRunAsync(runId, token),
                () => IsHandoffTargetCurrent(target),
                cancellation.Token);
            if (!preparation.IsReady)
            {
                ShowAgentInlineStatus(preparation.Error ?? "The selected saved run could not be verified.");
                return;
            }

            var content = preparation.Content!;
            var source = preparation.SourceRun!;
            if (await ConfirmHandoffPreviewAsync(source, content) != ContentDialogResult.Primary
                || cancellation.IsCancellationRequested
                || !IsHandoffTargetCurrent(target))
            {
                return;
            }

            AddHandoffAttachment(source, content, target);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (IsHandoffTargetCurrent(target))
            {
                ShowAgentInlineStatus("Preparing saved work context timed out; your draft was kept.");
            }
        }
        catch (Exception exception)
        {
            if (IsHandoffTargetCurrent(target))
            {
                ShowAgentInlineStatus($"Could not prepare saved work context: {exception.Message}");
            }
        }
        finally
        {
            cancellation.Dispose();
            _isChoosingWorkStateHandoff = false;
            HandoffWorkStateButton.IsEnabled = true;
        }
    }

    private async Task<SessionInfo?> SelectHandoffSessionAsync(IReadOnlyList<SessionInfo> sessions)
    {
        var picker = new ComboBox { MinWidth = 380, PlaceholderText = "Choose a source session" };
        foreach (var session in sessions.Take(100))
        {
            var label = string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title;
            picker.Items.Add(new ComboBoxItem
            {
                Content = $"{label} · {session.UpdatedAt}",
                Tag = session
            });
        }

        var dialog = new ContentDialog
        {
            Title = "Choose saved work context",
            Content = picker,
            PrimaryButtonText = "Choose session",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || picker.SelectedItem is not ComboBoxItem { Tag: SessionInfo selectedSession })
        {
            return null;
        }
        return selectedSession;
    }

    private async Task<string?> SelectHandoffRunAsync(IReadOnlyList<ApiService.SessionRunRef> runs)
    {
        var picker = new ComboBox { MinWidth = 380, PlaceholderText = "Choose a source run" };
        foreach (var run in runs.Reverse().Take(100))
        {
            picker.Items.Add(new ComboBoxItem
            {
                Content = $"{run.Status} · {run.CreatedAt?.ToLocalTime():g} · {run.RunId}",
                Tag = run.RunId
            });
        }

        var dialog = new ContentDialog
        {
            Title = "Choose source run",
            Content = picker,
            PrimaryButtonText = "Preview",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || picker.SelectedItem is not ComboBoxItem { Tag: string runId })
        {
            return null;
        }
        return runId;
    }

    private async Task<ContentDialogResult> ConfirmHandoffPreviewAsync(AgentRunModel source, string content)
    {
        var dialog = new ContentDialog
        {
            Title = $"Preview context from {source.WorkStateSourceRunId}",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap }
            },
            PrimaryButtonText = "Attach to this chat",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync();
    }

    private WorkStateHandoffTarget CaptureHandoffTarget() => new(
        _activeSessionId,
        _activeWorkspaceId,
        _agentTurnGeneration,
        _sessionOpenGeneration,
        ChatInputTextBox.Text,
        PendingAttachments.Select(attachment => attachment.Id).ToList());

    private bool IsHandoffTargetCurrent(WorkStateHandoffTarget target) =>
        WorkStateHandoffCoordinator.IsCurrent(
            target,
            _activeSessionId,
            _activeWorkspaceId,
            _agentTurnGeneration,
            _sessionOpenGeneration,
            ChatInputTextBox.Text,
            PendingAttachments.Select(attachment => attachment.Id).ToList());

    private void AddHandoffAttachment(AgentRunModel source, string content, WorkStateHandoffTarget target)
    {
        var attachment = new ImageAttachmentItem
        {
            FileName = $"saved-work-context-{source.WorkStateSourceRunId}.txt",
            IsImage = false,
            TextContent = content,
            ByteSize = System.Text.Encoding.UTF8.GetByteCount(content)
        };
        if (!TryAddAttachmentToComposer(attachment, target.SessionId, target.SessionGeneration))
        {
            ShowAgentInlineStatus("The saved context could not be retained in this draft.");
        }
    }
}
