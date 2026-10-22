using RuApp.Models;

namespace RuApp.Services;

/// <summary>A failed read is not an authoritative empty list. Retain identities for Stop and retry.</summary>
public sealed class BackgroundTaskObservation
{
    private int _failures;
    public IReadOnlyList<AgentTaskRow> Rows { get; private set; } = [];
    public string Status { get; private set; } = "No background tasks";
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Min(30, 2 << _failures));
    public bool ShouldPoll(bool mainRunRunning) => mainRunRunning || _failures > 0 || Rows.Any(row => row.State == "running");

    public void Apply(AgentTaskProjectionResult result)
    {
        Rows = result.Rows;
        Status = result.Status;
        _failures = 0;
    }

    public void Fail()
    {
        _failures = Math.Min(4, _failures + 1);
        Status = Rows.Count > 0
            ? "Refresh unavailable · showing last known tasks. Retrying…"
            : "Tasks unavailable. Retrying…";
    }

    public void Reset() => Apply(new([], "No background tasks"));
}
