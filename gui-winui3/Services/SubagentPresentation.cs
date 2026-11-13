using System.ComponentModel;
using System.Globalization;
using RuAgent.Protocol.Subagents;

namespace RuApp.Services;

public enum SubagentGroup { Working, Attention, Finished }
public sealed record SubagentRow(string AgentId, string Role, string Title, string RoleLabel,
    string Status, SubagentGroup Group, string Tone) : INotifyPropertyChanged
{
    private string _meta = "";
    public string AgentId { get; } = AgentId;
    public string Role { get; } = Role;
    public string Title { get; } = Title;
    public string RoleLabel { get; } = RoleLabel;
    public string Status { get; } = Status;
    public SubagentGroup Group { get; } = Group;
    public string Tone { get; } = Tone;
    /// <summary>
    /// Elapsed time, steps and tokens. Mutable so a ticking duration updates the bound
    /// row in place instead of replacing the list item (which would drop keyboard focus).
    /// </summary>
    public string Meta
    {
        get => _meta;
        set
        {
            if (_meta == value) return;
            _meta = value;
            PropertyChanged?.Invoke(this, new(nameof(Meta)));
            PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
            PropertyChanged?.Invoke(this, new(nameof(AutomationLabel)));
        }
    }
    public string Subtitle => Meta.Length == 0 ? $"{RoleLabel} · {Status}" : $"{RoleLabel} · {Status} · {Meta}";
    public string AutomationLabel => Meta.Length == 0 ? $"{Title}, {RoleLabel}, {Status}" : $"{Title}, {RoleLabel}, {Status}, {Meta}";
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Everything but <see cref="Meta"/>; record equality would also compare subscribers.</summary>
    public bool SameState(SubagentRow other) => AgentId == other.AgentId && Role == other.Role && Title == other.Title
        && RoleLabel == other.RoleLabel && Status == other.Status && Group == other.Group && Tone == other.Tone;
}

/// <summary>Presentation truth, independent of controls and API transport.</summary>
public static class SubagentPresentation
{
    public const int MaxPreviewBytes = 256 * 1024;

    /// <summary>Refresh cadence while children are admitted, collecting or preparing.</summary>
    public static readonly TimeSpan ActiveRefresh = TimeSpan.FromSeconds(2);
    /// <summary>Fallback cadence while the root can still delegate; change hints refresh sooner.</summary>
    public static readonly TimeSpan IdleRefresh = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How soon to read the snapshot again, or null once nothing can change: the root has
    /// completed, failed or been cancelled and every child's last attempt has too, with its
    /// result published. An interrupted run can be recovered under the same id, and a
    /// failed or cancelled attempt's result is published on a later Host tick, so neither
    /// counts as settled.
    /// </summary>
    public static TimeSpan? RefreshInterval(SubagentListResponse? snapshot)
    {
        if (snapshot is null) return ActiveRefresh;
        if (snapshot.Preparations.Count > 0 || snapshot.Agents.Any(agent => Row(agent).Group == SubagentGroup.Working))
            return ActiveRefresh;
        var settled = snapshot.UnavailableAttempts == 0 && IsFinal(snapshot.RootStatus) && snapshot.Agents.All(agent =>
            agent.Attempts.LastOrDefault() is { } attempt && IsFinal(attempt.Status)
            && agent.Results.Any(result => result.RunId == attempt.RunId));
        return settled ? null : IdleRefresh;
    }

    private static bool IsFinal(string status) => status is "completed" or "failed" or "cancelled";

    /// <param name="now">The observer's clock for a still-running attempt; omit for a stable row.</param>
    public static SubagentRow Row(SubagentSummary agent, DateTimeOffset? now = null)
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
        var label = string.IsNullOrWhiteSpace(agent.Profile) ? role : $"{role} ({agent.Profile})";
        return new(agent.AgentId, agent.Role, Title(agent.Task, role), label, status, group, tone)
        { Meta = Meta(attempt, group == SubagentGroup.Working ? now : null) };
    }

    /// <summary>"4m 12s · 12 steps · 34.5k tokens"; each part only when the contract supplies it.</summary>
    public static string Meta(SubagentAttemptSummary? attempt, DateTimeOffset? now)
    {
        if (attempt is null) return "";
        var parts = new List<string>();
        if (Elapsed(attempt, now) is { } elapsed) parts.Add(Duration(elapsed));
        if (attempt.Usage is { ModelSteps: > 0 } usage) parts.Add(usage.ModelSteps == 1 ? "1 step" : $"{usage.ModelSteps:N0} steps");
        if (attempt.Usage?.TotalTokens is > 0 and var tokens) parts.Add(CompactTokens(tokens) + " tokens");
        return string.Join(" · ", parts);
    }

    /// <summary>Admission to end; for a live attempt, admission (or creation while queued) to now.</summary>
    public static TimeSpan? Elapsed(SubagentAttemptSummary attempt, DateTimeOffset? now)
    {
        if ((attempt.AdmittedAt ?? attempt.CreatedAt) is not { } start) return null;
        if ((attempt.EndedAt ?? now) is not { } end || end < start) return null;
        return end - start;
    }

    public static string Duration(TimeSpan elapsed)
    {
        var seconds = (long)elapsed.TotalSeconds;
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60:00}s";
        return $"{seconds / 3600}h {seconds % 3600 / 60:00}m";
    }

    private static string CompactTokens(long tokens) => tokens < 1000
        ? tokens.ToString(CultureInfo.InvariantCulture)
        : tokens < 1_000_000
            ? (tokens / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : (tokens / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M";

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
        var rows = snapshot?.Agents.Select(agent => Row(agent)).ToArray() ?? [];
        if (snapshot?.Preparations.Count is > 0) parts.Add($"{snapshot.Preparations.Count} preparing");
        if (snapshot?.UnavailableAttempts is > 0) parts.Add($"{snapshot.UnavailableAttempts} unavailable");
        if (snapshot is { Running: > 0, MaxRunnable: > 0 })
            parts.Add($"active {snapshot.Running}/{snapshot.MaxRunnable}");
        if (rows.Length > 0) parts.Add(CompactSummary(rows));
        return parts.Count > 0 ? string.Join(" · ", parts) : "No subagents yet";
    }
}
