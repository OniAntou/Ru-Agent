using RuAgent.Client;

namespace RuApp.Services;

public sealed record AgentContextEstimateEventArgs(string RunId, string SessionId, AgentUsageEstimateModel Estimate);
