using System.Text.Json;
using System.Text.Json.Serialization;
using RuAgent.Protocol;

namespace RuAgent.Broker.Runtime;

/// <summary>
/// Callback-scoped orchestration permission. Only Broker can construct it; it is
/// not a wire DTO or a transferable token and cannot authorize file/terminal tools.
/// </summary>
public sealed class AuthorizedSubagentControl
{
    private readonly Func<bool> _isCurrent;
    private readonly CancellationToken _cancellation;
    private int _active = 1;

    internal AuthorizedSubagentControl(string rootRunId, string sessionId, string toolId, string invocationId,
        JsonElement arguments, Func<bool> isCurrent, CancellationToken cancellation)
    {
        RootRunId = rootRunId;
        SessionId = sessionId;
        ToolId = toolId;
        InvocationId = invocationId;
        Arguments = arguments.Clone();
        _isCurrent = isCurrent;
        _cancellation = cancellation;
    }

    [JsonIgnore] public string RootRunId { get; }
    [JsonIgnore] public string SessionId { get; }
    [JsonIgnore] public string ToolId { get; }
    [JsonIgnore] public string InvocationId { get; }
    [JsonIgnore] public JsonElement Arguments { get; }

    public void DemandActive()
    {
        if (Volatile.Read(ref _active) == 0) throw new AgentProtocolException("subagent_control_stale");
        _cancellation.ThrowIfCancellationRequested();
        if (!_isCurrent()) throw new AgentProtocolException("subagent_control_stale");
    }

    internal void Expire() => Interlocked.Exchange(ref _active, 0);
}
