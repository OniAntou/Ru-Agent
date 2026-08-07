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
    public async Task ReplyAudioQueueAcceptsNextChunkWhileCurrentChunkIsPlaying()
    {
        var firstChunkStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstChunk = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var played = new List<int>();

        using var queue = new VoiceReplyAudioQueue(
            async (chunk, cancellationToken) =>
            {
                if (chunk.Sequence == 0)
                {
                    firstChunkStarted.TrySetResult(true);
                    await releaseFirstChunk.Task.WaitAsync(cancellationToken);
                }

                lock (played)
                {
                    played.Add(chunk.Sequence);
                }
            });

        await queue.EnqueueAsync(Chunk("r1", 0));
        await firstChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await queue.EnqueueAsync(Chunk("r1", 1)).WaitAsync(TimeSpan.FromSeconds(1));

        releaseFirstChunk.TrySetResult(true);
        await queue.CompleteAsync();

        Assert.Equal(new[] { 0, 1 }, played);
    }

    [Fact]
    public void StartAndCancelFramesContainOnlyProtocolFields()
    {
        var start = VoiceStreamClient.SerializeStartFrame("r1", "Chào anh.", "Ru (OmniVoice)", 1.0);
        var cancel = VoiceStreamClient.SerializeCancelFrame("r1");
        var playback = VoiceStreamClient.SerializePlaybackFrame("r1", "ended");

        Assert.Contains("\"type\":\"start\"", start);
        Assert.Contains("\"request_id\":\"r1\"", start);
        Assert.Contains("\"type\":\"cancel\"", cancel);
        Assert.Contains("\"type\":\"playback\"", playback);
        Assert.Contains("\"state\":\"ended\"", playback);
        Assert.DoesNotContain("timing", start, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("audio", cancel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SentenceBufferEmitsCompletedSentencesBeforeReplyEnds()
    {
        var buffer = new VoiceReplySentenceBuffer();

        Assert.Equal(new[] { "Câu đầu." }, buffer.Append("Câu đầu. Câu"));
        Assert.Empty(buffer.Append(" thứ hai"));
        Assert.Equal(new[] { "Câu thứ hai." }, buffer.Append("."));
        Assert.Empty(buffer.Append(" Phần còn lại"));
        Assert.Equal(new[] { "Phần còn lại" }, buffer.Flush());
    }

    [Fact]
    public void SentenceBufferEmitsOneWordSafeOpeningPhraseWithoutPunctuation()
    {
        var buffer = new VoiceReplySentenceBuffer();

        Assert.Equal(
            new[] { "Dạ anh, em sẽ kiểm tra kỹ phần quan trọng này trước" },
            buffer.Append("Dạ anh, em sẽ kiểm tra kỹ phần quan trọng này trước rồi báo lại cho anh ngay khi có kết quả"));
    }

    [Theory]
    [InlineData("Dạ anh, em sẽ đọc chính xác giá trị quan trọng là 1 000 000đồngngaybâygiờkhôngtách")]
    [InlineData("Dạ anh, em sẽ đọc chính xác giá trị quan trọng là 1\u00A0000\u00A0000đồngngaybâygiờkhôngtách")]
    public void SentenceBufferDoesNotSplitGroupedNumbersAtWhitespace(string text)
    {
        var buffer = new VoiceReplySentenceBuffer();

        Assert.Empty(buffer.Append(text));
    }

    [Theory]
    [InlineData("Dạ anh, em sẽ giữ nguyên [voice marker nội bộ tuyệt đối không được tách cho tới đây] rồi trả lời")]
    [InlineData("Dạ anh, em sẽ giữ nguyên (voice marker nội bộ tuyệt đối không được tách cho tới đây) rồi trả lời")]
    [InlineData("Dạ anh, em sẽ giữ nguyên {voice marker nội bộ tuyệt đối không được tách cho tới đây} rồi trả lời")]
    [InlineData("Dạ anh, em sẽ giữ nguyên <voice marker nội bộ tuyệt đối không được tách cho tới đây> rồi trả lời")]
    [InlineData("Dạ anh, em sẽ giữ nguyên `voice marker nội bộ tuyệt đối không được tách cho tới đây` rồi trả lời")]
    [InlineData("Dạ anh, em sẽ giữ nguyên **voice marker nội bộ tuyệt đối không được tách cho tới đây** rồi trả lời")]
    [InlineData("Dạ anh, em sẽ giữ nguyên _voice marker nội bộ tuyệt đối không được tách cho tới đây_ rồi trả lời")]
    [InlineData("Dạ anh, em sẽ giữ nguyên ~~voice marker nội bộ tuyệt đối không được tách cho tới đây~~ rồi trả lời")]
    public void SentenceBufferDoesNotSplitInsideDelimitedOrBacktickTokens(string text)
    {
        var buffer = new VoiceReplySentenceBuffer();

        Assert.Empty(buffer.Append(text));
    }

    [Theory]
    [InlineData("[meta. still protected]")]
    [InlineData("(meta! still protected)")]
    [InlineData("{meta? still protected}")]
    [InlineData("<meta. still protected>")]
    [InlineData("`meta. still protected`")]
    [InlineData("```meta. still protected```")]
    [InlineData("*meta. still protected*")]
    [InlineData("**meta. still protected**")]
    [InlineData("_meta. still protected_")]
    [InlineData("__meta. still protected__")]
    [InlineData("~~meta. still protected~~")]
    public void SentenceBufferDoesNotEmitPunctuationInsideProtectedSpans(string text)
    {
        var buffer = new VoiceReplySentenceBuffer();

        Assert.Empty(buffer.Append(text));
        Assert.Equal(new[] { text }, buffer.Flush());
    }

    [Fact]
    public void SentenceBufferUsesFirstSafeFallbackBoundaryFrom61Through72()
    {
        var buffer = new VoiceReplySentenceBuffer();

        Assert.Equal(
            new[] { "Dạ anh, em sẽ kiểm tra kỹ phần quan trọng nàytrướckhôngtáchdài" },
            buffer.Append("Dạ anh, em sẽ kiểm tra kỹ phần quan trọng nàytrướckhôngtáchdài rồi báo lại cho anh"));
    }

    [Fact]
    public void SentenceBufferDoesNotTreatUnmatchedAngleAsAnOpenToken()
    {
        var buffer = new VoiceReplySentenceBuffer();
        string prefix = new('a', 48);
        string input = $"{prefix} < threshold rồi báo lại cho anh";

        Assert.Equal(new[] { $"{prefix} < threshold" }, buffer.Append(input));
    }

    [Fact]
    public void SentenceBufferIgnoresFormattingDelimiterInsideProtectedToken()
    {
        var buffer = new VoiceReplySentenceBuffer();
        string prefix = new('a', 32);
        string input = $"{prefix}[internal * marker] then chạy tiếp cho anh";

        Assert.Equal(new[] { $"{prefix}[internal * marker] then" }, buffer.Append(input));
    }

    [Fact]
    public void SentenceBufferReturnsToSentenceBoundariesAfterOpeningPhrase()
    {
        var buffer = new VoiceReplySentenceBuffer();
        buffer.Append("Dạ anh, em sẽ kiểm tra kỹ phần quan trọng này trước rồi báo lại cho anh ngay khi có kết quả");

        Assert.Empty(buffer.Append(" và không để sót điểm nào"));
        Assert.Equal(
            new[] { "rồi báo lại cho anh ngay khi có kết quả và không để sót điểm nào." },
            buffer.Append("."));
    }

    [Fact]
    public void SentenceBufferUsesFirstSafeFallbackBoundaryAfterGroupedNumber()
    {
        var buffer = new VoiceReplySentenceBuffer();
        string prefix = new('a', 47);
        const string groupedNumber = "1\u00A0000\u00A0000";
        string input = $"{prefix} {groupedNumber} sau đó em sẽ trả lời đầy đủ hơn cho anh";

        Assert.Equal(
            new[] { $"{prefix} {groupedNumber} sau" },
            buffer.Append(input));
    }

    [Fact]
    public void SentenceBufferDoesNotSplitDelimitedTokenAtInternalWhitespace()
    {
        var buffer = new VoiceReplySentenceBuffer();
        string input = $"{new string('a', 48)}[voice marker token] continuing words after this payload";

        Assert.Empty(buffer.Append(input));
        Assert.Equal(new[] { input }, buffer.Flush());
    }

    private static VoiceAudioChunk Chunk(string requestId, int sequence) =>
        new(requestId, sequence, new byte[] { 0x52, 0x49, 0x46, 0x46 }, 24_000, "wav");
}
