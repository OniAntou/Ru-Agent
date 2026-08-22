namespace RuApp.Services;

public sealed record AgentActivityItem(
    string State,
    string SafeSummary,
    string SessionId,
    DateTimeOffset Timestamp);
