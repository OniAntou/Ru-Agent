namespace RuApp.Harness;

public sealed record HarnessGuiCheck(
    string Id,
    string Status,
    double DurationMs,
    string? Reason = null);

public sealed record HarnessGuiResult(
    int SchemaVersion,
    string Scenario,
    IReadOnlyList<HarnessGuiCheck> Checks,
    IReadOnlyDictionary<string, int> EventCounts,
    bool ExistingInstanceDetected,
    bool AppClosed);
