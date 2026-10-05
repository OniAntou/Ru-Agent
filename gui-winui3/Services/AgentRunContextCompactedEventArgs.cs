namespace RuApp.Services;

public sealed record AgentRunContextCompactedEventArgs(string RunId, string SessionId, long? Sequence);
