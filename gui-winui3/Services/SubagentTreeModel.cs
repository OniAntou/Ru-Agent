using RuAgent.Protocol.Subagents;

namespace RuApp.Services;

public readonly record struct SubagentReadTicket(string Root, long Generation, long Sequence);
public readonly record struct SubagentResultReadTicket(string Root, long Generation, long Selection,
    string AgentId, long Version, long Sequence);

/// <summary>UI-independent root ownership and stale-response gate.</summary>
public sealed class SubagentTreeModel
{
    private string _root = "";
    private long _generation;
    private long _request;
    private long _applied;
    public SubagentListResponse? Snapshot { get; private set; }
    private long _selection;
    private long _resultRequest;
    public string? SelectedAgentId { get; private set; }
    public long? SelectedVersion { get; private set; }
    public SubagentSummary? Selected => Snapshot?.Agents.SingleOrDefault(a => a.AgentId == SelectedAgentId);
    public SubagentResultSummary? Result => Selected?.Results.SingleOrDefault(r => r.ResultVersion == SelectedVersion);

    public void Bind(string root, bool reset = false)
    {
        if (_root == root && !reset) return;
        _root = root;
        _generation++;
        _applied = 0;
        Snapshot = null;
        ShowList();
    }
    public SubagentReadTicket BeginRead() => new(_root, _generation, ++_request);
    public bool Apply(SubagentReadTicket ticket, SubagentListResponse snapshot)
    {
        if (ticket.Root != _root || ticket.Generation != _generation || ticket.Sequence <= _applied
            || snapshot.RootRunId != _root || snapshot.Agents.Count > 8) return false;
        _applied = ticket.Sequence;
        if (Snapshot?.SnapshotId == snapshot.SnapshotId) return false;
        var wasPending = Selected is { Results.Count: 0 };
        Snapshot = snapshot;
        if (SelectedAgentId is not null && Selected is null) ShowList();
        else if (SelectedVersion is null && wasPending && Selected?.Results.LastOrDefault() is { } first)
            SelectVersion(first.ResultVersion);
        return true;
    }
    public bool SelectAgent(string id)
    {
        var agent = Snapshot?.Agents.SingleOrDefault(a => a.AgentId == id);
        if (agent is null) return false;
        SelectedAgentId = id;
        SelectedVersion = agent.Results.LastOrDefault()?.ResultVersion;
        _selection++;
        return true;
    }
    public bool SelectVersion(long version)
    {
        if (Selected?.Results.Any(r => r.ResultVersion == version) != true) return false;
        SelectedVersion = version;
        _selection++;
        return true;
    }
    public void ShowList()
    {
        SelectedAgentId = null;
        SelectedVersion = null;
        _selection++;
    }
    public SubagentResultReadTicket BeginResultRead() => new(_root, _generation, _selection,
        SelectedAgentId ?? "", SelectedVersion ?? 0, ++_resultRequest);
    public bool IsCurrent(SubagentResultReadTicket ticket) => ticket.Root == _root && ticket.Generation == _generation
        && ticket.Selection == _selection && ticket.AgentId == SelectedAgentId && ticket.Version == SelectedVersion
        && ticket.Sequence == _resultRequest && Result is not null;

    public static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled" or "interrupted";
    public static string Describe(SubagentSummary agent)
    {
        var attempt = agent.Attempts.LastOrDefault();
        return $"{agent.Role} · {attempt?.Status ?? "unavailable"} · {attempt?.ProviderId ?? "provider unavailable"}/{attempt?.Model ?? "model unavailable"}\n{agent.Task ?? agent.AgentId}";
    }
}
