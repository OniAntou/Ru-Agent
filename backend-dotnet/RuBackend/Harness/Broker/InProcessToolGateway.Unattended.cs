using System.Text.Json.Nodes;

namespace RuBackend.Harness.Broker;

/// <summary>Asks the execution authority whether a call would run without an approval.</summary>
public interface IUnattendedToolPolicy
{
    bool AllowsWithoutApproval(string toolId, string toolVersion, JsonObject arguments);
}

public sealed partial class InProcessToolGateway : IUnattendedToolPolicy
{
    public bool AllowsWithoutApproval(string toolId, string toolVersion, JsonObject arguments)
    {
        BrokerRunLeaseIdentity lease;
        lock (_sync)
        {
            if (IsDisposed) return false;
            lease = _lease;
        }

        return _runtime.AllowsWithoutApproval(lease.SessionId, _runId, toolId, toolVersion, arguments);
    }
}
