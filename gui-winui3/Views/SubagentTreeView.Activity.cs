using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The selected child's conversation: the brief it was given as the user's message and its
/// own run replay as the reply, drawn with the main chat's message templates and re-read on
/// the panel's refresh tick while the attempt runs.
/// </summary>
/// <remarks>
/// Observation only, like the rest of the browser: nothing here approves, steers or stops
/// the child, and its rows offer no rewind. The templates come from the chat view that hosts
/// the panel, so a subagent's work reads exactly like the main agent's.
/// </remarks>
public sealed partial class SubagentTreeView
{
    private readonly SubagentTranscript _transcript = new();
    private string _activityFingerprint = "";
    private bool _readingActivity;
    // The loaded attempt ended before it was read: its replay cannot change again.
    private bool _activitySettled;

    /// <summary>The main chat's message template selector; the transcript renders with it.</summary>
    public DataTemplateSelector? TranscriptTemplateSelector
    {
        get => ChildTranscript.ItemTemplateSelector;
        set => ChildTranscript.ItemTemplateSelector = value;
    }

    private void ClearChildActivity()
    {
        _activitySettled = false;
        _activityFingerprint = "";
        _transcript.Clear();
        ChildTranscript.Visibility = Visibility.Collapsed;
        UpdateResultTextVisibility();
    }

    private async Task RefreshChildActivityAsync()
    {
        if (_disposed || _readingActivity || _client is null || _detailRead is null
            || _model.Selected is not { } agent || ViewedAttempt(agent, _model.Result) is not { RunId: { Length: > 0 } runId } attempt
            || (_activitySettled && runId == _transcript.RunId)) return;
        // Decided before the read: an attempt that ends mid-read is read once more.
        var settled = SubagentTreeModel.IsTerminal(attempt.Status);
        var client = _client;
        var ct = _detailRead.Token;
        var agentId = agent.AgentId;
        _readingActivity = true;
        try
        {
            var replay = await client.ListChildActivityAsync(runId, ct);
            if (ct.IsCancellationRequested || _model.Selected is not { } current || current.AgentId != agentId
                || ViewedAttempt(current, _model.Result) is not { } viewed || viewed.RunId != runId) return;
            var fingerprint = Fingerprint(current.Task, viewed, replay);
            if (runId == _transcript.RunId && fingerprint == _activityFingerprint)
            {
                // Settled only once a read made after the attempt ended repeats the previous
                // one, so a row written just after the terminal commit is still picked up.
                _activitySettled = settled;
                return;
            }
            _activityFingerprint = fingerprint;
            _transcript.Apply(
                new SubagentAttemptView(runId, current.Task, viewed.Status, viewed.AdmittedAt ?? viewed.CreatedAt, viewed.EndedAt),
                replay, AgentActivityDisplayPreferences.Instance.Compact);
            ChildTranscript.Visibility = _transcript.Messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderPendingResult();
            UpdateResultTextVisibility();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Advisory: the result and status above stay authoritative.
            GuiDiagnosticWriter.AppendTraceLine($"[Subagents] Activity read failed: {error.GetType().Name}");
        }
        finally { _readingActivity = false; }
    }

    /// <summary>
    /// What a read changes on screen. Row state is included, so a row that only moved from
    /// running to done is still applied; the attempt's status ends the live reply.
    /// </summary>
    internal static string Fingerprint(string? task, RuAgent.Protocol.Subagents.SubagentAttemptSummary attempt,
        RuAgent.Client.AgentRunActivityReplay replay) =>
        string.Join("\n",
            [task ?? "", attempt.Status, attempt.EndedAt?.ToString("O") ?? "",
             .. replay.Activities.Select(row => (row with { Todos = null }) + $"#{row.Todos?.Count}"),
             .. replay.Transcript.Select(entry => $"{entry.Sequence}:{entry.Kind}:{entry.ActivityId}:{entry.Text?.Length}")]);

    /// <summary>
    /// A completed result is the child's final reply, which the transcript already shows, so
    /// its paged text is drawn only when the replay holds no words. A failed or cancelled
    /// result says how the attempt ended and is always drawn.
    /// </summary>
    private void UpdateResultTextVisibility() =>
        ResultTextPanel.Visibility = _transcript.ShowsPublishedText(_model.Result?.Status) ? Visibility.Visible : Visibility.Collapsed;

    private static Microsoft.UI.Xaml.Media.Brush? Brush(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as Microsoft.UI.Xaml.Media.Brush : null;
}
