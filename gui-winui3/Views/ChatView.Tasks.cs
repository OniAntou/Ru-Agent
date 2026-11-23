using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;
using RuAgent.Client;

namespace RuApp.Views;

/// <summary>
/// The right side surface's background-task and edit data. Rows come from the
/// Host's typed <c>/tasks</c> command and a stop is the same command with a kill
/// argument, so the Broker stays the only execution authority and the view
/// never talks to it directly. The summary card and the details panel render
/// the same observation, with a bounded card preview; only the panel offers Stop.
/// </summary>
public sealed partial class ChatView
{
    private bool? _sidePanelRunRunning;
    private bool _sidePanelEditsDirty = true;
    private string? _sidePanelTasksRunId;
    private bool _taskRefreshInProgress;
    private readonly UiRequestLifetime _taskRefreshRequests = new();
    private CancellationTokenSource? _taskRefreshCancellation;
    private readonly BackgroundTaskObservation _taskObservation = new();
    private string? _taskObservationRunId;

    /// <summary>
    /// Keeps the surface fresh from run-state updates: the title and edit
    /// projections retain unchanged rows. Visible task summaries also refresh
    /// periodically so processes can settle independently of the model's run.
    /// </summary>
    /// <summary>
    /// A newer title for the open session (one titled by its first prompt after it was
    /// opened). Titles for other sessions are ignored.
    /// </summary>
    public void UpdateSessionTitle(string sessionId, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)
            || !string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal)
            || string.Equals(title, _activeSessionTitle, StringComparison.Ordinal))
        {
            return;
        }

        _activeSessionTitle = title;
        SidePanel?.SetSessionTitle(title);
        UpdateChatHeaderTitle();
    }

    private void UpdateSidePanel(bool isRunRunning)
    {
        if (SidePanel is null)
        {
            return;
        }

        SidePanel.SetSessionTitle(_activeSessionTitle);
        SidePanel.SetWorkspace(_agentWorkspace);
        UpdateChatHeaderWorkspace();
        SidePanel.SetTerminalWorkingDirectory(string.IsNullOrWhiteSpace(_agentWorkspace) ? null : _agentWorkspace);
        ApplySidePanelState();
        UpdateWelcomeWorkspaceText();
        RefreshSidePanelEdits();

        if (_sidePanelRunRunning != isRunRunning)
        {
            _sidePanelRunRunning = isRunRunning;
            _ = RefreshAgentTasksAsync();
        }
        UpdateTaskRefreshTimer(IsSidePanelDesiredVisible());
    }

    /// <summary>Rebuilds the Changes tab rows and the shared +added −removed totals.</summary>
    private void RefreshSidePanelEdits()
    {
        if (SidePanel is null || !_sidePanelEditsDirty)
        {
            return;
        }

        var projection = ChangesTabProjection.ProjectSession(Messages, AgentActivities, _focusedChangeActivityId);
        SidePanel.RenderChanges(projection.Rows, projection.Added, projection.Removed);
        _sidePanelEditsDirty = false;
    }

    private async Task RefreshAgentTasksAsync()
    {
        var version = _taskRefreshRequests.Replace();
        _taskRefreshInProgress = false;
        _taskRefreshCancellation?.Cancel();
        _taskRefreshCancellation?.Dispose();
        _taskRefreshCancellation = null;
        if (SidePanel is null)
        {
            return;
        }

        SidePanel.SetTasksRefreshing(false);

        var runId = ResolveSidePanelTaskRunId();
        if (!string.Equals(runId, _taskObservationRunId, StringComparison.Ordinal))
        {
            _taskObservationRunId = runId;
            _taskObservation.Reset();
            RenderTaskObservation();
        }
        var controller = _agentController;
        var sessionGeneration = _sessionOpenGeneration;
        bool OwnsRefresh() => !_scrollingDisposed
            && _taskRefreshRequests.Owns(version)
            && sessionGeneration == _sessionOpenGeneration
            && ReferenceEquals(controller, _agentController)
            && string.Equals(runId, ResolveSidePanelTaskRunId(), StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(runId) || _agentController is null)
        {
            _taskObservation.Reset();
            RenderTaskObservation();
            return;
        }

        try
        {
            _taskRefreshInProgress = true;
            SidePanel.SetTasksRefreshing(true);
            _taskRefreshCancellation = new CancellationTokenSource();
            var response = await controller!
                .DispatchCommandAsync(runId, "/tasks", _taskRefreshCancellation.Token)
                .ConfigureAwait(true);
            if (!OwnsRefresh()) return;
            if (!string.Equals(response.Status, "succeeded", StringComparison.Ordinal))
            {
                _taskObservation.Fail();
                RenderTaskObservation();
                return;
            }

            RenderAgentTasks(response.Data);
        }
        catch (AgentApiException error) when (error.ErrorCode == "broker_gateway_unavailable")
        {
            if (!OwnsRefresh()) return;
            // A retained run may have no live gateway after restart or before its
            // first tool. Preserve uncertainty; active runs/tasks still drive polling.
            _taskObservation.Fail(retryable: false);
            RenderTaskObservation();
        }
        catch (Exception)
        {
            if (!OwnsRefresh()) return;
            _taskObservation.Fail();
            RenderTaskObservation();
        }
        finally
        {
            if (_taskRefreshRequests.Owns(version))
            {
                _taskRefreshInProgress = false;
                SidePanel.SetTasksRefreshing(false);
                UpdateTaskRefreshTimer(IsSidePanelDesiredVisible());
            }
        }
    }

    private void RenderAgentTasks(object? data)
    {
        _taskObservation.Apply(AgentTaskProjection.Project(data));
        RenderTaskObservation();
    }

    /// <summary>
    /// Opens one task's retained output. The Host pages it through the same
    /// typed command that lists tasks, so the view still never talks to the
    /// Broker and the model and the reader see the same redacted text.
    /// </summary>
    private async void OnSidePanelTaskLogRequested(object? sender, string executionId)
    {
        var controller = _agentController;
        var runId = ResolveSidePanelTaskRunId();
        if (controller is null || string.IsNullOrWhiteSpace(runId))
        {
            return;
        }

        long generation = _sessionOpenGeneration;
        try
        {
            // No offset asks the Host for the tail page: the end of a log is
            // what a reader needs first when a task failed.
            var page = await LoadTaskOutputAsync(controller, runId, executionId, offset: null).ConfigureAwait(true);
            if (generation != _sessionOpenGeneration)
            {
                return;
            }

            if (page is null)
            {
                SidePanel.SetTasksStatus("Could not read this task's output. Refresh to check its state.");
                return;
            }

            await ShowTaskOutputDialogAsync(controller, runId, executionId, page).ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Task output failed: {error.GetType().Name}");
            SidePanel.SetTasksStatus("Could not read this task's output.");
        }
    }

    private async Task<AgentTaskOutput?> LoadTaskOutputAsync(
        AgentRunController controller,
        string runId,
        string executionId,
        int? offset)
    {
        var command = "/tasks read " + executionId
            + (offset is { } start ? " " + start.ToString(CultureInfo.InvariantCulture) : string.Empty);
        var response = await controller.DispatchCommandAsync(runId, command).ConfigureAwait(true);
        return string.Equals(response.Status, "succeeded", StringComparison.Ordinal)
            ? AgentTaskOutputProjection.Project(response.Data)
            : null;
    }

    /// <summary>
    /// The bounded reader: one page at a time, with Earlier/Later walking the
    /// neighbouring windows the response names. The dialog stays open while it
    /// pages, so the reader keeps its scroll position context.
    /// </summary>
    private async Task ShowTaskOutputDialogAsync(
        AgentRunController controller,
        string runId,
        string executionId,
        AgentTaskOutput page)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var header = new TextBlock
        {
            Text = page.Header,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        };
        var body = new TextBox
        {
            Text = FormatTaskOutput(page),
            IsReadOnly = true,
            AcceptsReturn = true,
            IsSpellCheckEnabled = false,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = TaskLogFont(),
            MinWidth = 560,
            MaxWidth = 760,
        };
        var dialog = new ContentDialog
        {
            Title = "Task output",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    header,
                    new ScrollViewer
                    {
                        Content = body,
                        MaxHeight = 420,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                },
            },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        ApplyTaskOutputButtons(dialog, page);

        var paging = false;
        async void Page(int offset)
        {
            if (paging)
            {
                return;
            }

            paging = true;
            try
            {
                var next = await LoadTaskOutputAsync(controller, runId, executionId, offset).ConfigureAwait(true);
                if (next is null)
                {
                    header.Text = "That page is no longer retained.";
                    return;
                }

                page = next;
                header.Text = page.Header;
                body.Text = FormatTaskOutput(page);
                ApplyTaskOutputButtons(dialog, page);
            }
            catch (Exception error)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Task output page failed: {error.GetType().Name}");
                header.Text = "Could not load that page.";
            }
            finally
            {
                paging = false;
            }
        }

        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            Page(Math.Max(0, page.Offset - page.Limit));
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            Page(page.NextOffset);
        };

        try
        {
            await ContentDialogGate.ShowAsync(dialog).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            // WinUI throws when another dialog is already open; an async void
            // handler would take the dispatcher down with it.
            GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Task output dialog failed: {error.GetType().Name}");
            SidePanel.SetTasksStatus("Could not open the task output.");
        }
    }

    private static void ApplyTaskOutputButtons(ContentDialog dialog, AgentTaskOutput page)
    {
        dialog.PrimaryButtonText = page.HasBefore ? "Earlier" : string.Empty;
        dialog.SecondaryButtonText = page.HasMore ? "Later" : string.Empty;
    }

    private static string FormatTaskOutput(AgentTaskOutput page) =>
        page.Page.Length > 0 ? page.Page : "(no retained output)";

    /// <summary>Monospace when the app resource is available; the system default otherwise.</summary>
    private static FontFamily TaskLogFont()
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue("RuMonoFontFamily", out var value) == true
                && value is FontFamily family)
            {
                return family;
            }
        }
        catch (Exception)
        {
            // Resource lookup is cosmetic; fall through to the default family.
        }

        return new FontFamily("Consolas");
    }

    // Task data stays "unavailable", never "empty". With no live run and nothing known, the
    // alarming wording gives way to a plain note on when the list can fill in.
    private void RenderTaskObservation()
    {
        var quiet = _taskObservation.GatewayUnavailable
            && _taskObservation.Rows.Count == 0
            && _sidePanelRunRunning != true;
        SidePanel.RenderTasks(
            _taskObservation.Rows,
            quiet ? "Tasks appear here while a run is active." : _taskObservation.Status);
    }

}
