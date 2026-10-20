using System.Text.Json;
using RuAgent.Cli.TerminalGui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgSessionTitleReaderTests
{
    [Theory]
    [InlineData("{\"session\":{\"title\":\"  Current task  \"}}", "Current task")]
    [InlineData("{\"session\":{\"title\":\"  \"}}", null)]
    [InlineData("{\"session\":{\"title\":null}}", null)]
    [InlineData("{}", null)]
    [InlineData("[]", null)]
    public async Task Reads_only_a_nonempty_session_title(string json, string? expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, await TgSessionTitleReader.ReadAsync(_ => Task.FromResult(doc.RootElement), default));
    }

    [Fact]
    public async Task A_stalled_metadata_request_is_cancelled_by_its_own_deadline()
    {
        var cancelled = false;
        var title = await TgSessionTitleReader.ReadAsync(async token =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return default; }
            finally { cancelled = token.IsCancellationRequested; }
        }, default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(title);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task User_cancellation_and_transport_failures_remain_distinguishable_from_timeout()
    {
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = TgSessionTitleReader.ReadAsync(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return default;
        }, stop.Token);
        await started.Task;
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAsync<HttpRequestException>(() => TgSessionTitleReader.ReadAsync(
            _ => Task.FromException<JsonElement>(new HttpRequestException("synthetic failure")), default));
    }
}
