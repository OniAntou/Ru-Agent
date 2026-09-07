using RuApp.Models;

namespace RuApp.Services;

public sealed record AgentUsageReceivedEventArgs(
    string RunId,
    string SessionId,
    AgentModelUsage Usage);
