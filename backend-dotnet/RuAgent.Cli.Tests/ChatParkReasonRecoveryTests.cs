using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatParkReasonRecoveryTests
{
    private const string Run = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Theory]
    [InlineData("other_run", 3, "waiting_user", "continue.requested")]
    [InlineData(Run, 4, "waiting_user", "continue.requested")]
    [InlineData(Run, 3, "running", "continue.requested")]
    [InlineData(Run, 3, "waiting_user", "run.state_changed")]
    public async Task Unrelated_events_are_not_presented_as_the_park_reason(string run, long seq, string state, string type)
    {
        using var client = Client((_, _) => Task.FromResult(Response(Frame(run, seq, state, type))));
        Assert.Null(await ChatParkReasonRecovery.ReadAsync(client, Run, 2, CancellationToken.None));
    }

    [Fact]
    public async Task Replay_is_finite_authenticated_and_bound_to_the_exact_cursor()
    {
        using var client = Client((request, _) =>
        {
            Assert.Contains("after_sequence=2&live=0", request.RequestUri!.Query);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(Response(Frame(Run, 3, "waiting_user", "continue.requested")));
        });
        Assert.NotNull(await ChatParkReasonRecovery.ReadAsync(client, Run, 2, CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_is_bounded_and_parent_cancellation_is_not_swallowed()
    {
        using var client = Client(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        Assert.Null(await ChatParkReasonRecovery.ReadAsync(client, Run, 2, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8)));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChatParkReasonRecovery.ReadAsync(client, Run, 2, cancelled.Token));
    }

    [Fact]
    public async Task Failed_optional_replay_keeps_the_generic_park_notice()
    {
        using var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("unavailable") }));
        Assert.Null(await ChatParkReasonRecovery.ReadAsync(client, Run, 2, CancellationToken.None));
        Assert.False(ChatParkReasonRecovery.NeedsReplay(new(3, "continue.requested", "{}", false, null)));
    }

    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
    private static string Frame(string run, long seq, string state, string type) =>
        $"id: {seq}\ndata: {JsonSerializer.Serialize(new HarnessSafeEventEnvelope(1, "evt_1", run, "sess_1", seq, type, "2026-10-12T00:00:00Z", state, "provider_timeout"))}\n\n";
    private static AgentSseClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var descriptor = new RuntimeDescriptor(1, "http://127.0.0.1:8000/", new string('e', 64), process.Id,
            process.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O"));
        return new(PublicHostSession.FromDescriptor(descriptor), new HttpClient(new Handler(send)));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
