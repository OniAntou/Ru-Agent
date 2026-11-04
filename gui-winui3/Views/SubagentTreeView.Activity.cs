using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The selected child's own timeline: its narration and tool rows, read from the child
/// run's durable replay on the panel's existing refresh tick. Before this the detail
/// pane said only "Working on this task" until a result was published.
/// </summary>
/// <remarks>
/// Observation only, like the rest of the browser: no row here can be clicked, approved
/// or stopped. Thinking rows are left out; they carry no work a reader can follow.
/// </remarks>
public sealed partial class SubagentTreeView
{
    internal const int MaxChildActivityLines = 40;
    private string? _activityRunId;
    private string _activityFingerprint = "";
    private bool _readingActivity;

    private void ClearChildActivity()
    {
        _activityRunId = null;
        _activityFingerprint = "";
        ChildActivityList.Children.Clear();
        ChildActivityPanel.Visibility = Visibility.Collapsed;
    }

    private async Task RefreshChildActivityAsync()
    {
        if (_disposed || _readingActivity || _client is null || _detailRead is null
            || _model.Selected is not { } agent || agent.Attempts.LastOrDefault()?.RunId is not { Length: > 0 } runId) return;
        var client = _client;
        var ct = _detailRead.Token;
        var agentId = agent.AgentId;
        _readingActivity = true;
        try
        {
            var replay = await client.ListChildActivityAsync(runId, ct);
            if (ct.IsCancellationRequested || _model.SelectedAgentId != agentId) return;
            var lines = ChildActivityLines(replay);
            var fingerprint = string.Join("\n", lines.Select(line => (line.IsNarration ? "n:" : line.Tone + ":") + line.Text));
            if (runId == _activityRunId && fingerprint == _activityFingerprint) return;
            _activityRunId = runId;
            _activityFingerprint = fingerprint;
            ChildActivityList.Children.Clear();
            foreach (var line in lines) ChildActivityList.Children.Add(Render(line));
            ChildActivityPanel.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Advisory: the result and status above stay authoritative.
            GuiDiagnosticWriter.AppendTraceLine($"[Subagents] Activity read failed: {error.GetType().Name}");
        }
        finally { _readingActivity = false; }
    }

    internal sealed record ChildActivityLine(string Text, bool IsNarration, string Tone);

    /// <summary>The replay in event order, newest last, bounded to the most recent lines.</summary>
    internal static IReadOnlyList<ChildActivityLine> ChildActivityLines(AgentRunActivityReplay replay)
    {
        var byId = replay.Activities.GroupBy(row => row.ActivityId).ToDictionary(group => group.Key, group => group.Last());
        var lines = new List<ChildActivityLine>();
        if (replay.Transcript.Count > 0)
        {
            foreach (var entry in replay.Transcript)
            {
                if (entry.Kind == "text" && !string.IsNullOrWhiteSpace(entry.Text))
                    lines.Add(new(entry.Text.Trim(), true, "muted"));
                else if (entry.ActivityId is { } id && byId.TryGetValue(id, out var row) && Line(row) is { } line)
                    lines.Add(line);
            }
        }
        else
        {
            foreach (var row in replay.Activities)
                if (Line(row) is { } line) lines.Add(line);
        }

        return lines.Count <= MaxChildActivityLines ? lines : lines.GetRange(lines.Count - MaxChildActivityLines, MaxChildActivityLines);
    }

    private static ChildActivityLine? Line(AgentActivityModel row)
    {
        if (row.Kind is AgentActivityKind.Thinking) return null;
        var item = new AgentActivityItem(row);
        var text = string.IsNullOrWhiteSpace(item.MetadataText) ? item.DisplayText : $"{item.DisplayText} · {item.MetadataText}";
        var tone = item.IsFailed ? "danger" : item.IsRunning || item.IsWaiting ? "live" : "done";
        return new(text, false, tone);
    }

    private static TextBlock Render(ChildActivityLine line) => new()
    {
        Text = line.IsNarration ? line.Text : "› " + line.Text,
        FontSize = line.IsNarration ? 13 : 12,
        FontWeight = line.Tone == "live" ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Foreground = Brush(line.IsNarration ? "RuInkBrush" : line.Tone == "danger" ? "RuDangerBrush" : "RuMutedBrush"),
    };

    private static Brush? Brush(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as Brush : null;
}
