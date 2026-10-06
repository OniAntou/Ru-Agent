using System;
using RuAgent.Client;

namespace RuApp.Services;

public sealed class AgentRunActivityEventArgs(
    string runId,
    long ownershipGeneration,
    AgentActivityModel activity) : EventArgs
{
    public string RunId { get; } = runId;
    public long OwnershipGeneration { get; } = ownershipGeneration;
    public AgentActivityModel Activity { get; } = activity;
}

public sealed class AgentRunSummaryEventArgs(
    string runId,
    long ownershipGeneration,
    AgentRunSummaryModel summary) : EventArgs
{
    public string RunId { get; } = runId;
    public long OwnershipGeneration { get; } = ownershipGeneration;
    public AgentRunSummaryModel Summary { get; } = summary;
}
