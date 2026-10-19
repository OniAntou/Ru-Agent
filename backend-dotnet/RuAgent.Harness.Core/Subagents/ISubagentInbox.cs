namespace RuAgent.Harness.Core.Subagents;

public interface ISubagentInbox
{
    Task<int> DeliverMessagesAsync(RunId runId, DateTimeOffset now, CancellationToken ct);
}
