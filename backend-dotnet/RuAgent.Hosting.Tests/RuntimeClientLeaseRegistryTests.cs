using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class RuntimeClientLeaseRegistryTests
{
    [Fact]
    public void LeaseCountTracksChatAndSse()
    {
        var registry = new RuntimeClientLeaseRegistry();
        Assert.Equal(0, registry.LeaseCount);
        var chat = registry.Acquire("chat");
        Assert.Equal(1, registry.LeaseCount);
        var sse = registry.Acquire("sse");
        Assert.Equal(2, registry.LeaseCount);
        chat.Dispose();
        Assert.Equal(1, registry.LeaseCount);
        sse.Dispose();
        Assert.Equal(0, registry.LeaseCount);
    }

    [Fact]
    public void DisposeReleasesOnce()
    {
        var registry = new RuntimeClientLeaseRegistry();
        var lease = registry.Acquire("chat");
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0, registry.LeaseCount);
    }

    [Fact]
    public void HasLeasesReflectsLifetime()
    {
        var registry = new RuntimeClientLeaseRegistry();
        Assert.False(registry.HasLeases);
        var lease = registry.Acquire("agent.sse");
        Assert.True(registry.HasLeases);
        lease.Dispose();
        Assert.False(registry.HasLeases);
    }
}
