using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoicePlaybackQueueTests
{
    [Fact]
    public async Task QueueRejectsStaleRequestsAndSequenceGaps()
    {
        var played = new List<int>();
        using var queue = new VoicePlaybackQueue(
            (chunk, _) =>
            {
                played.Add(chunk.Sequence);
                return Task.CompletedTask;
            });

        await queue.StartAsync("r1");

        Assert.True(await queue.EnqueueAsync("r1", Chunk("r1", 0)));
        Assert.False(await queue.EnqueueAsync("old", Chunk("old", 1)));
        Assert.False(await queue.EnqueueAsync("r1", Chunk("r1", 2)));
        await queue.CompleteAsync("r1");

        Assert.Equal(new[] { 0 }, played);
    }

    [Fact]
    public void StartAndCancelFramesContainOnlyProtocolFields()
    {
        var start = VoiceStreamClient.SerializeStartFrame("r1", "Chào anh.", "Ru (OmniVoice)", 1.0);
        var cancel = VoiceStreamClient.SerializeCancelFrame("r1");

        Assert.Contains("\"type\":\"start\"", start);
        Assert.Contains("\"request_id\":\"r1\"", start);
        Assert.Contains("\"type\":\"cancel\"", cancel);
        Assert.DoesNotContain("timing", start, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audio", cancel, StringComparison.OrdinalIgnoreCase);
    }

    private static VoiceAudioChunk Chunk(string requestId, int sequence) =>
        new(requestId, sequence, new byte[] { 0x52, 0x49, 0x46, 0x46 }, 24_000, "wav");
}
