using System.Globalization;
using RuAgent.Protocol.Subagents;

namespace RuApp.Services;

public enum SubagentGroup { Working, Attention, Finished }
public sealed record SubagentRow(string AgentId, string Role, string Title, string RoleLabel,
    string Status, SubagentGroup Group, string Tone)
{
    public string AgentId { get; } = AgentId;
    public string Role { get; } = Role;
    public string Title { get; } = Title;
    public string RoleLabel { get; } = RoleLabel;
    public string Status { get; } = Status;
    public SubagentGroup Group { get; } = Group;
    public string Tone { get; } = Tone;
    public string Subtitle => $"{RoleLabel} · {Status}";
    public string AutomationLabel => $"{Title}, {RoleLabel}, {Status}";
}

/// <summary>Presentation truth, independent of controls and API transport.</summary>
public static class SubagentPresentation
{
    public const int MaxPreviewBytes = 256 * 1024;

    public static SubagentRow Row(SubagentSummary agent)
    {
        var attempt = agent.Attempts.LastOrDefault();
        var result = agent.Results.LastOrDefault(r => r.RunId == attempt?.RunId);
        var (group, status, tone) = attempt?.Status switch
        {
            null => (SubagentGroup.Attention, "Status unavailable", "attention"),
            _ when agent.Canceled && !SubagentTreeModel.IsTerminal(attempt.Status) => (SubagentGroup.Working, "Stopping", "muted"),
            "created" => (SubagentGroup.Working, "Queued", "working"),
            "running" => (SubagentGroup.Working, "Working", "working"),
            "completed" when result is null => (SubagentGroup.Working, "Collecting result", "working"),
            "completed" when result!.Code is not null || result.Status != "completed" => (SubagentGroup.Attention, "Result unavailable", "attention"),
            "completed" => (SubagentGroup.Finished, "Done", "done"),
            "cancelled" => (SubagentGroup.Finished, "Stopped", "muted"),
            "failed" => (SubagentGroup.Attention, "Failed", "attention"),
            "interrupted" => (SubagentGroup.Attention, "Interrupted", "attention"),
            "reconciliation_required" => (SubagentGroup.Attention, "Needs main agent", "attention"),
            "waiting_approval" or "waiting_user" or "waiting_input" or "waiting_workspace_trust" => (SubagentGroup.Attention, "Waiting on main agent", "attention"),
            "waiting_history" or "waiting_history_user" or "waiting_history_final" => (SubagentGroup.Working, "Saving history", "working"),
            "waiting_subagents" => (SubagentGroup.Attention, "Waiting on delegated work", "attention"),
            _ => (SubagentGroup.Attention, "Status unavailable", "attention"),
        };
        var role = agent.Role == "coding" ? "Coding" : "Review";
        return new(agent.AgentId, agent.Role, Title(agent.Task, role), role, status, group, tone);
    }

    public static string Title(string? task, string role)
    {
        if (string.IsNullOrWhiteSpace(task)) return role + " task";
        var line = task.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var sentence = line.IndexOf(". ", StringComparison.Ordinal);
        if (sentence > 0) line = line[..(sentence + 1)];
        var elements = StringInfo.ParseCombiningCharacters(line);
        return elements.Length <= 76 ? line : line[..elements[76]].TrimEnd() + "…";
    }

    public static string Summary(IReadOnlyList<SubagentRow> rows)
    {
        var parts = new List<string>();
        void Add(int count, string label) { if (count > 0) parts.Add($"{count} {label}"); }
        Add(rows.Count(r => r.Group == SubagentGroup.Working), "working");
        Add(rows.Count(r => r.Group == SubagentGroup.Attention), "need attention");
        Add(rows.Count(r => r.Status == "Done"), "done");
        Add(rows.Count(r => r.Status == "Stopped"), "stopped");
        return parts.Count == 0 ? "No subagents yet" : string.Join(" · ", parts);
    }

    public static string CompactSummary(IReadOnlyList<SubagentRow> rows)
    {
        var working = rows.Count(r => r.Group == SubagentGroup.Working);
        var attention = rows.Count(r => r.Group == SubagentGroup.Attention);
        if (working > 0) return $"{working} working" + (attention > 0 ? $" · {attention} waiting" : "");
        return attention > 0 ? $"{attention} need attention" : Summary(rows);
    }

    public static string Usage(SubagentUsageSummary? usage) => usage?.TotalTokens is { } tokens
        ? $"{tokens:N0} tokens reported" + (usage.IsComplete ? "" : " · incomplete")
        : "Token usage unavailable";

    public static string ObserverSummary(SubagentListResponse? snapshot, bool refreshFailed)
    {
        var parts = new List<string>();
        if (refreshFailed) parts.Add("Refresh unavailable");
        var rows = snapshot?.Agents.Select(Row).ToArray() ?? [];
        if (snapshot?.Preparations.Count is > 0) parts.Add($"{snapshot.Preparations.Count} preparing");
        if (snapshot?.UnavailableAttempts is > 0) parts.Add($"{snapshot.UnavailableAttempts} unavailable");
        if (rows.Length > 0) parts.Add(CompactSummary(rows));
        return parts.Count > 0 ? string.Join(" · ", parts) : "No subagents yet";
    }
}
