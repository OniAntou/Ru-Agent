using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class HarnessSessionDescriptorTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://192.168.1.10:8000")]
    public void Harness_descriptor_rejects_non_loopback_edge(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() =>
            HarnessSessionDescriptor.Create(baseUrl, "secret", "turn", "events.jsonl"));
    }
}
