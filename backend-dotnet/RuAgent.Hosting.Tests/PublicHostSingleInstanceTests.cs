using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class PublicHostSingleInstanceTests
{
    [Fact]
    public void NamedMutexEnsuresSingleInstance()
    {
        var name = "Local\\RuTestSingleInstance-" + Guid.NewGuid().ToString("N");
        using var first = new PublicHostSingleInstance(name);
        Assert.True(first.IsAcquired);

        using var second = new PublicHostSingleInstance(name);
        Assert.False(second.IsAcquired);
    }

    [Fact]
    public void ReleaseAllowsReacquire()
    {
        var name = "Local\\RuTestSingleInstance-" + Guid.NewGuid().ToString("N");
        var first = new PublicHostSingleInstance(name);
        Assert.True(first.IsAcquired);
        first.Dispose();

        using var second = new PublicHostSingleInstance(name);
        Assert.True(second.IsAcquired);
    }

    [Fact]
    public void DifferentNamesDoNotConflict()
    {
        var name1 = "Local\\RuTestSingleInstance-" + Guid.NewGuid().ToString("N");
        var name2 = "Local\\RuTestSingleInstance-" + Guid.NewGuid().ToString("N");
        using var a = new PublicHostSingleInstance(name1);
        using var b = new PublicHostSingleInstance(name2);
        Assert.True(a.IsAcquired);
        Assert.True(b.IsAcquired);
    }
}
