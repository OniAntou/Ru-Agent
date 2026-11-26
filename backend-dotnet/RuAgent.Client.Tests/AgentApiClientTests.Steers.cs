using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Client.Tests;

/// <summary>
/// A steer is retried only when its request may have been lost, and always with the id that
/// makes the retry a replay at the Host instead of a second message (ADR 0056).
/// </summary>
public sealed partial class AgentApiClientTests
{
    private const string SteerRun = "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string Steer = "steer_" + new string('d', 32);

    private static HttpResponseMessage SteerAccepted(string state = AgentSteerLimits.StatePending) =>
        new(HttpStatusCode.Accepted)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                run_id = SteerRun,
                steer_id = Steer,
                state,
                delivered_payload_id = "payload_" + new string('d', 32),
            }), Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task A_steer_whose_response_is_lost_is_retried_with_the_same_id()
    {
        var attempts = new List<string>();
        var handler = new FakeHandler(async request =>
        {
            attempts.Add(await request.Content!.ReadAsStringAsync());
            if (attempts.Count == 1) throw new HttpRequestException("connection reset");
            return SteerAccepted();
        });
        var client = new AgentApiClient(Session(), new HttpClient(handler));

        var result = await client.SteerAsync(SteerRun, Steer, "keep going", default);

        Assert.Equal(AgentSteerLimits.StatePending, result.State);
        Assert.Equal(2, attempts.Count);
        // The retry carries the same id and text, so it can only replay the stored steer.
        Assert.Equal(Steer, JsonDocument.Parse(attempts[0]).RootElement.GetProperty("steer_id").GetString());
        Assert.Equal(attempts[0], attempts[1]);
    }

    [Fact]
    public async Task A_retry_accepts_the_state_the_host_reports_for_the_stored_steer()
    {
        var handler = new FakeHandler(async request =>
        {
            await request.Content!.ReadAsStringAsync();
            return SteerAccepted(AgentSteerLimits.StateDelivered);
        });
        var client = new AgentApiClient(Session(), new HttpClient(handler));

        Assert.Equal(AgentSteerLimits.StateDelivered,
            (await client.SteerAsync(SteerRun, Steer, "already read", default)).State);
    }

    [Fact]
    public async Task A_steer_refused_by_the_host_is_not_retried()
    {
        var attempts = 0;
        var handler = new FakeHandler(_ =>
        {
            attempts++;
            return new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("""{"error_code":"steer_queue_full"}""", Encoding.UTF8, "application/json"),
            };
        });
        var client = new AgentApiClient(Session(), new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AgentApiException>(
            () => client.SteerAsync(SteerRun, Steer, "too many", default));

        Assert.Equal("steer_queue_full", error.ErrorCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task A_cancelled_steer_is_not_retried()
    {
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeHandler(new Func<HttpRequestMessage, HttpResponseMessage>(_ =>
        {
            attempts++;
            cancellation.Cancel();
            throw new TaskCanceledException("cancelled");
        }));
        var client = new AgentApiClient(Session(), new HttpClient(handler));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SteerAsync(SteerRun, Steer, "cancel me", cancellation.Token));

        Assert.Equal(1, attempts);
    }
}
