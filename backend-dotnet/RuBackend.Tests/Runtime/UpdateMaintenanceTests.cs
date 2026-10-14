using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using RuAgent.Harness.Core;
using RuAgent.Hosting;
using RuBackend.Tests.Harness;
using Xunit;

namespace RuBackend.Tests.Runtime;
public sealed class UpdateMaintenanceTests
{
    [Theory]
    [InlineData(HarnessRunState.WaitingApproval)]
    [InlineData(HarnessRunState.WaitingUser)]
    [InlineData(HarnessRunState.Created)]
    public async Task Pending_runs_refuse_update_and_restore_request_admission(HarnessRunState state)
    {
        await using var data = await BackendHarnessTestFixture.CreateAsync();
        using var factory = new BackendWebApplicationFactory();
        using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // These synthetic rows have no real retained approval payload. Keep
            // recovery/model workers from finishing them while this HTTP gate
            // test reads their durable nonterminal state. Scheduling has its own tests.
            services.RemoveAll<IHostedService>();
            services.RemoveAll<RuAgent.Harness.Persistence.HarnessSqliteOptions>();
            services.AddSingleton(data.Options);
        }));
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer","test-secret");
        await data.SeedRunAsync(new(state));
        using var response = await client.PostAsync("/host/shutdown?when_idle=true",null);
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
        Assert.False(response.Headers.Contains("X-Ru-Update-Ready"));
        Assert.Contains("update_busy",await response.Content.ReadAsStringAsync());
        Assert.True(configured.Services.GetRequiredService<HostActivityTracker>().TryEnter());
        configured.Services.GetRequiredService<HostActivityTracker>().Exit();
    }

    [Fact]
    public async Task Idle_update_acknowledges_and_stops_only_after_response()
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();
        var activity = factory.Services.GetRequiredService<HostActivityTracker>();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStopping.Register(() => stopped.TrySetResult());
        using var response = await client.PostAsync("/host/shutdown?when_idle=true",null);
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal("1",response.Headers.GetValues("X-Ru-Update-Ready").Single());
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(activity.TryEnter());
    }

    [Fact]
    public async Task Maintenance_requires_authentication()
    {
        using var factory = new BackendWebApplicationFactory(); using var client = factory.CreateClient();
        using var response = await client.PostAsync("/host/shutdown?when_idle=true",null);
        Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
    }
}
