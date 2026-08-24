using Xunit;

namespace RuAgent.Client.Tests;

public sealed class PublicHostLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-locator-" + Guid.NewGuid().ToString("N"));
    public PublicHostLocatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LocatorResolvesToRuDescriptorUnderDataRoot()
    {
        var locator = new PublicHostLocator(_root);
        Assert.EndsWith("runtime_descriptor.json", locator.DescriptorPath, StringComparison.Ordinal);
        Assert.Contains(_root, locator.DescriptorPath, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidDescriptorRejected()
    {
        var locator = new PublicHostLocator(_root);
        var path = locator.DescriptorPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"unknown\":1}");
        Assert.False(locator.TryGetValidDescriptor(out _, out var err));
        Assert.NotNull(err);
    }

    [Fact]
    public void ValidDescriptorAcceptedIfProcessAlive()
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1,
            $"http://127.0.0.1:{8000 + Random.Shared.Next(1000)}/",
            new string('a', 64),
            proc.Id,
            proc.StartTime.ToUniversalTime().ToString("O"),
            new string('b', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        var json = System.Text.Json.JsonSerializer.Serialize(desc, RuAgent.Protocol.Harness.RuntimeDescriptor.JsonOptions);
        var locator = new PublicHostLocator(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(locator.DescriptorPath)!);
        File.WriteAllText(locator.DescriptorPath, json);
        Assert.True(locator.TryGetValidDescriptor(out var read, out _));
        Assert.NotNull(read);
        Assert.Equal(desc.BearerToken, read!.BearerToken);
    }

    [Fact]
    public void StalePidRejected()
    {
        var locator = new PublicHostLocator(_root);
        var desc = new RuAgent.Protocol.Harness.RuntimeDescriptor(
            1,
            "http://127.0.0.1:8000/",
            new string('a', 64),
            999999,
            DateTimeOffset.UtcNow.ToString("O"),
            new string('c', 32),
            DateTimeOffset.UtcNow.ToString("O"));
        var json = System.Text.Json.JsonSerializer.Serialize(desc, RuAgent.Protocol.Harness.RuntimeDescriptor.JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(locator.DescriptorPath)!);
        File.WriteAllText(locator.DescriptorPath, json);
        Assert.False(locator.TryGetValidDescriptor(out _, out var err));
        Assert.Equal("stale_pid", err);
    }

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }
}
