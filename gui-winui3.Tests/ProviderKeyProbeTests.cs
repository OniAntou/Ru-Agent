using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ProviderKeyProbeTests
{
    [Fact]
    public async Task Superseded_edit_does_not_publish_the_validation_result()
    {
        using var probe = new ProviderKeyProbe();
        var gate = new TaskCompletionSource<ProviderKeyProbeResult>();
        CancellationToken requestToken = default;
        var pending = probe.RunAsync("models.dev:a", "synthetic", (_, _, ct) =>
        {
            requestToken = ct;
            return gate.Task;
        });
        Assert.True(probe.IsRunning);
        probe.Invalidate();
        Assert.True(requestToken.IsCancellationRequested);
        // Even a transport which ignores cancellation cannot publish an old verdict.
        gate.SetResult(new("valid"));
        var completion = Assert.IsType<ProviderKeyProbeCompletion>(await pending);
        Assert.Equal("superseded", completion.Result.Code);
        Assert.True(completion.RetryRequested);
        Assert.False(probe.IsRunning);
    }

    [Theory]
    [InlineData("models.dev:a", " ")]
    [InlineData("models.dev:", "synthetic")]
    [InlineData("models.dev:bad/id", "synthetic")]
    [InlineData("custom", "synthetic")]
    [InlineData("unknown", "synthetic")]
    public async Task Ineligible_edits_do_not_send_credentials(string choice, string key)
    {
        using var probe = new ProviderKeyProbe();
        Assert.Null(await probe.RunAsync(choice, key,
            (_, _, _) => throw new InvalidOperationException("Must not send")));
        Assert.False(probe.IsRunning);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("invalid")]
    [InlineData("unverified")]
    [InlineData("save_failed")]
    public async Task Current_edit_reports_the_atomic_host_result(string code)
    {
        using var probe = new ProviderKeyProbe();
        var expected = new ProviderKeyProbeResult(code);
        var completion = Assert.IsType<ProviderKeyProbeCompletion>(await probe.RunAsync("Models.Dev:OpenRouter", "synthetic", (id, key, ct) =>
        {
            Assert.Equal("models.dev:openrouter", id);
            Assert.Equal("synthetic", key);
            Assert.True(ct.CanBeCanceled);
            return Task.FromResult(expected);
        }));
        Assert.Same(expected, completion.Result);
        Assert.Equal("openrouter", completion.Target.ModelsDevId);
        Assert.False(completion.RetryRequested);
        Assert.False(probe.IsRunning);
    }

    [Fact]
    public async Task Concurrent_probe_is_skipped_and_next_edit_can_run_after_cancellation()
    {
        using var probe = new ProviderKeyProbe();
        var gate = new TaskCompletionSource<ProviderKeyProbeResult>();
        var pending = probe.RunAsync("models.dev:a", "old-synthetic", (_, _, _) => gate.Task);
        probe.Invalidate();
        Assert.Null(await probe.RunAsync("models.dev:b", "new-synthetic",
            (_, _, _) => throw new InvalidOperationException("Must not overlap")));
        gate.SetResult(new("invalid"));
        Assert.True((await pending)!.RetryRequested);
        var next = await probe.RunAsync("models.dev:b", "new-synthetic",
            (_, _, _) => Task.FromResult(new ProviderKeyProbeResult("valid")));
        Assert.Equal("valid", next!.Result.Code);
        Assert.Equal("models.dev:b", next.Target.Choice);
        Assert.False(next.RetryRequested);
    }

    [Fact]
    public async Task Disposal_cancels_without_requesting_retry_or_accepting_another_probe()
    {
        var probe = new ProviderKeyProbe();
        var gate = new TaskCompletionSource<ProviderKeyProbeResult>();
        var pending = probe.RunAsync("models.dev:a", "synthetic", (_, _, ct) =>
        {
            ct.Register(() => gate.TrySetCanceled(ct));
            return gate.Task;
        });
        probe.Dispose();
        probe.Dispose();
        var completion = (await pending)!;
        Assert.Equal("superseded", completion.Result.Code);
        Assert.False(completion.RetryRequested);
        Assert.False(probe.IsRunning);
        Assert.Null(await probe.RunAsync("models.dev:a", "synthetic",
            (_, _, _) => throw new InvalidOperationException("Must not send")));
    }

    [Fact]
    public async Task Unexpected_failure_releases_the_single_flight_slot()
    {
        using var probe = new ProviderKeyProbe();
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.RunAsync(
            "models.dev:a", "synthetic", (_, _, _) => throw new InvalidOperationException("test")));
        Assert.False(probe.IsRunning);
        var next = await probe.RunAsync("models.dev:a", "synthetic",
            (_, _, _) => Task.FromResult(new ProviderKeyProbeResult("valid")));
        Assert.Equal("valid", next!.Result.Code);
    }
}
