using RuAgent.Protocol.Harness;
using RuApp.Models;

namespace RuApp.Services;

internal sealed record AgentActivitySummary(int Count, int Failed, int Waiting, string Label)
{
    internal static readonly AgentActivitySummary Empty = new(0, 0, 0, string.Empty);
}

/// <summary>One turn's activity status; narration remains in the reply text.</summary>
internal static class AgentActivitySummaryProjection
{
    internal static AgentActivitySummary ForMessage(ChatMessageItem? message)
    {
        if (message?.Role != "assistant" || message.AgentActivities is not { } activities)
            return AgentActivitySummary.Empty;

        int count = 0, failed = 0, waiting = 0;
        AgentActivityItem? running = null;
        foreach (var item in activities)
        {
            if (item.Kind == AgentActivityKind.Narration) continue;
            count++;
            if (item.IsFailed) failed++;
            if (item.IsWaiting) waiting++;
            if (item.IsRunning) running = item;
        }
        if (count == 0) return AgentActivitySummary.Empty;

        var label = count == 1 ? "1 activity" : $"{count} activities";
        if (waiting > 0) label = $"Waiting · {label}";
        else if (message.IsStreaming) label = $"Working · {label}";
        if (failed > 0) label += $" · {failed} failed";
        if (message.IsStreaming && waiting == 0 && running is not null)
        {
            var current = running.DisplayText.Replace('\r', ' ').Replace('\n', ' ');
            label += " · " + (current.Length > 160 ? current[..160] + "…" : current);
        }
        return new(count, failed, waiting, label);
    }
}
