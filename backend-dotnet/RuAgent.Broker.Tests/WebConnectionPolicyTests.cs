using System.Net;
using RuAgent.Broker.Tools.Web;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class WebConnectionPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.0.10")]
    [InlineData("169.254.10.10")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void Private_addresses_are_never_connectable(string literal)
    {
        Assert.Throws<UnauthorizedAccessException>(() =>
            WebConnectionPolicy.SelectPublicAddresses(new[] { IPAddress.Parse(literal) }));
    }

    [Fact]
    public void Public_addresses_pass_and_private_ones_are_filtered()
    {
        var selected = WebConnectionPolicy.SelectPublicAddresses(new[]
        {
            IPAddress.Parse("10.0.0.5"),
            IPAddress.Parse("93.184.216.34"),
            IPAddress.Parse("127.0.0.1"),
        });

        Assert.Equal(new[] { IPAddress.Parse("93.184.216.34") }, selected);
    }

    [Fact]
    public async Task A_rebinding_answer_with_only_private_targets_never_reaches_connect()
    {
        var failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => WebConnectionPolicy.OpenValidatedConnectionAsync(
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") }),
                "rebind.test",
                80,
                CancellationToken.None).AsTask());

        Assert.Equal("web_private_host", failure.Message);
    }
}
