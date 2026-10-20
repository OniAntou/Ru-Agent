using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ProviderKeyProbeTests
{
    [Fact]
    public async Task Superseded_edit_does_not_publish_the_validation_result()
    {
        var gate = new TaskCompletionSource<ProviderKeyProbeResult>();
        bool owns = true;
        var pending = ProviderKeyProbe.RunAsync("models.dev:a", "synthetic", () => owns, (_, _) => gate.Task);
        owns = false;
        gate.SetResult(new("valid"));
        Assert.Equal("superseded", (await pending).Code);
    }

    [Fact]
    public async Task Already_superseded_edit_does_not_send_a_credential()
    {
        var result = await ProviderKeyProbe.RunAsync("models.dev:a", "synthetic", () => false,
            (_, _) => throw new InvalidOperationException("Must not send"));
        Assert.Equal("superseded", result.Code);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("invalid")]
    [InlineData("unverified")]
    [InlineData("save_failed")]
    public async Task Current_edit_reports_the_atomic_host_result(string code)
    {
        var expected = new ProviderKeyProbeResult(code);
        var result = await ProviderKeyProbe.RunAsync("models.dev:a", "synthetic", () => true, (id, key) =>
        {
            Assert.Equal("models.dev:a", id);
            Assert.Equal("synthetic", key);
            return Task.FromResult(expected);
        });
        Assert.Same(expected, result);
    }
}
