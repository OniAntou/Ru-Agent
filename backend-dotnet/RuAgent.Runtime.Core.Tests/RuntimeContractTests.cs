using RuAgent.Runtime.Core.Contracts;
using RuAgent.Runtime.Core.Lifecycle;
using Xunit;

namespace RuAgent.Runtime.Core.Tests;

public sealed class RuntimeContractTests
{
    [Fact]
    public void Voice_request_rejects_non_original_style_and_out_of_range_speed()
    {
        Assert.Throws<ArgumentException>(() => new VoiceRequest("hello", "ru", 1.0, "fast"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoiceRequest("hello", "ru", 2.1, "original"));
    }

    [Fact]
    public void Voice_audio_requires_mono_24_kilohertz_non_empty_wav()
    {
        Assert.Throws<ArgumentException>(() => new VoiceAudio(ReadOnlyMemory<byte>.Empty, 24_000, 1));
        Assert.Throws<ArgumentException>(() => new VoiceAudio(new byte[4], 16_000, 1));
        Assert.Throws<ArgumentException>(() => new VoiceAudio(new byte[4], 24_000, 2));
    }

    [Fact]
    public void Vts_catalog_normalizes_codes_and_rejects_duplicate_actions()
    {
        var catalog = new VtsCatalog("model-1", ["HotKeyA"], ["Smile"]);

        Assert.Equal(new[] { "hotkeya", "smile" }, catalog.AllCodes.OrderBy(value => value));
        Assert.Throws<ArgumentException>(() => catalog.ValidateActionCodes(["smile", "SMILE"]));
        Assert.Equal(new[] { "smile" }, catalog.ValidateActionCodes(["SMILE"]));
    }

    [Fact]
    public void Observation_clamps_numeric_values_and_bounds_visible_text()
    {
        var observation = new VisionObservation(
            "scene",
            "event",
            2.0,
            -1.0,
            0.5,
            Enumerable.Repeat("text", 12).ToArray(),
            "reaction");

        Assert.Equal(1.0, observation.Confidence);
        Assert.Equal(0.0, observation.Novelty);
        Assert.Equal(8, observation.VisibleText.Count);
    }

    [Fact]
    public async Task Lease_coordinator_serializes_same_resource_until_release()
    {
        await using var coordinator = new RuntimeLeaseCoordinator();
        await using var first = await coordinator.AcquireAsync(ResourceKind.GpuAudio, CancellationToken.None);
        using var acquired = new ManualResetEventSlim();
        var second = Task.Run(async () =>
        {
            await using var lease = await coordinator.AcquireAsync(ResourceKind.GpuAudio, CancellationToken.None);
            acquired.Set();
        });

        Assert.False(acquired.Wait(TimeSpan.FromMilliseconds(100)));
        await first.DisposeAsync();
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(2)));
        await second;
    }
}
