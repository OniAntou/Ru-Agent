using RuApp.Models;

namespace RuApp.Services;

public sealed record AgentUsageReceivedEventArgs(
    string RunId,
    string SessionId,
    AgentModelUsage Usage,
    long? Sequence,
    string? ModelStepId = null);

public sealed record AgentModelStepStartedEventArgs(string RunId, string SessionId, long Sequence, string? ModelStepId);
