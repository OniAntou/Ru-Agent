using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RuBackend.Harness;
using RuBackend.Harness.Reconciliation;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentServiceCompositionTests
{
    [Fact]
    public void Production_host_resolves_policy_and_recovery_services_with_required_dependencies()
    {
        using var factory = new BackendWebApplicationFactory();
        _ = factory.CreateClient();

        Assert.NotNull(factory.Services.GetRequiredService<AgentRunWorkspaceAdmissionService>());
        Assert.NotNull(factory.Services.GetRequiredService<AgentRunCreationService>());
        Assert.NotNull(factory.Services.GetRequiredService<AgentRunQueryService>());
        Assert.NotNull(factory.Services.GetRequiredService<AgentRunLifecycleService>());
        Assert.NotNull(factory.Services.GetRequiredService<AgentApprovalsService>());
        Assert.NotNull(factory.Services.GetRequiredService<AgentPoliciesService>());
        Assert.NotNull(factory.Services.GetRequiredService<AgentWorkspaceService>());
        Assert.NotNull(factory.Services.GetRequiredService<AgentWorkspaceTrustService>());
        Assert.NotNull(factory.Services.GetRequiredService<AgentActivitiesService>());
        Assert.NotNull(factory.Services.GetRequiredService<AgentCommandsService>());
        Assert.NotNull(factory.Services.GetRequiredService<AgentInputsService>());
        Assert.IsType<AgentRuntimeCoordinatorFactory>(factory.Services.GetRequiredService<IAgentRuntimeCoordinatorFactory>());
        Assert.Contains(factory.Services.GetServices<IHostedService>(), service => service is HarnessRecoveryStartupService);
    }
}
