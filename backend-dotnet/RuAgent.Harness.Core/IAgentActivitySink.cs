namespace RuAgent.Harness.Core;

/// <summary>
/// Receives bounded, non-durable Agent activity projections. Implementations must not
/// turn activity into a history, audit or model-context payload.
/// </summary>
public interface IAgentActivitySink
{
    void Publish(RunId runId, AgentActivity activity);
}
