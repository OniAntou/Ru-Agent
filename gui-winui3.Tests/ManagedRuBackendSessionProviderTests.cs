using System;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ManagedRuBackendSessionProviderTests
{
    [Fact]
    public void Provider_InitialState_HasDisplayNameAndNullSession()
    {
        using var provider = new ManagedRuBackendSessionProvider();

        Assert.Equal("Public Host (managed)", provider.DisplayName);
        Assert.Null(provider.Session);
    }

    [Fact]
    public async Task Provider_WhenDisposed_ThrowsObjectDisposedExceptionOnStart()
    {
        var provider = new ManagedRuBackendSessionProvider();
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.StartAsync("any-root", CancellationToken.None));
    }

    [Fact]
    public void Provider_NotifyBackendDown_FiresBackendExitedAndClearsSession()
    {
        using var provider = new ManagedRuBackendSessionProvider();
        bool exitedFired = false;
        provider.BackendExited += (_, _) => exitedFired = true;

        provider.NotifyBackendDown();

        Assert.True(exitedFired);
        Assert.Null(provider.Session);
    }

    [Fact]
    public async Task ExternalRuBackendSessionProvider_ReturnsConfiguredSession()
    {
        var session = BackendSession.FromLoopback(18080, "secret-token");
        using var provider = new ExternalRuBackendSessionProvider(session);

        Assert.Equal("Harness edge", provider.DisplayName);
        Assert.Same(session, provider.Session);

        var started = await provider.StartAsync("dummy", CancellationToken.None);
        Assert.Same(session, started);
    }
}
