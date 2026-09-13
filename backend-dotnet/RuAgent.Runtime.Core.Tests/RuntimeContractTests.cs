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

    [Fact]
    public async Task Lease_coordinator_try_acquire_reports_busy_without_waiting()
    {
        await using var coordinator = new RuntimeLeaseCoordinator();
        await using var first = await coordinator.AcquireAsync(ResourceKind.Observation, CancellationToken.None);

        Assert.False(coordinator.TryAcquire(ResourceKind.Observation, out var second));
        Assert.Null(second);
    }
}
