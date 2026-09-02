using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Tests.Conformance;
using RuBackend.Harness.Broker;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class HarnessToolGatewayConformanceTests : ToolGatewayConformance
{
    protected override Task<IToolGateway> CreateGatewayAsync(string workspaceRoot, string preset)
    {
        IToolGateway gateway = new HarnessToolGateway(
            workspaceRoot,
            AdvertisedCapabilities,
            presetProvider: _ => preset == FullAccessPreset
                ? AgentPermissionPreset.FullAccess
                : AgentPermissionPreset.Standard,
            idGenerator: null);
        return Task.FromResult(gateway);
    }
}
