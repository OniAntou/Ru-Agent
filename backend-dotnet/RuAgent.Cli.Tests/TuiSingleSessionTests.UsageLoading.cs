using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class TuiSingleSessionTests
{
    [Fact]
    public async Task Late_saved_usage_cannot_replace_the_new_turns_context_reading()
    {
        var savedRun = "run_" + new string('d', 32);
        var savedPayload = "payload_" + new string('1', 32);
        var livePayload = "payload_" + new string('2', 32);
        var savedPayloadRequested = NewSignal();
        var allowSavedPayload = NewSignal();
        var savedPayloadReturned = NewSignal();
        using var host = new SingleHost { ReadTurnPage = true, SavedMessages = SavedTurn(savedRun) };
        host.SeedCompletedRun(savedRun, "session_2");
        host.ReplayResponse = async (path, _) =>
        {
            if (path == $"/agent/v1/runs/{savedRun}/events")
                return UsageEventStream(savedRun, savedPayload, 42_000, completed: false);
            if (path == $"/agent/v1/runs/{savedRun}/payloads/{savedPayload}")
            {
                savedPayloadRequested.TrySetResult();
                // A transport can still finish an already-started read after cancellation.
                await allowSavedPayload.Task;
                savedPayloadReturned.TrySetResult();
                return UsagePayload(savedRun, savedPayload, 42_000);
            }
            if (path.EndsWith($"/payloads/{livePayload}", StringComparison.Ordinal))
                return UsagePayload(path.Split('/')[4], livePayload, 4_321);
            if (path.StartsWith("/agent/v1/runs/", StringComparison.Ordinal) && path.EndsWith("/events", StringComparison.Ordinal))
            {
                var liveRun = path.Split('/')[4];
                host.Streams["session_2"] = liveRun;
                host.Complete(liveRun);
                return UsageEventStream(liveRun, livePayload, 4_321, completed: true);
            }
            return null;
        };

        var stage = 0;
        var settledTicks = 0;
        try
        {
            await Run(host, (app, page) =>
            {
                switch (stage)
                {
                    case 0:
                        Send(app, page, "/sessions id:session_2");
                        stage++;
                        break;
                    case 1 when savedPayloadRequested.Task.IsCompleted
                                && page.Hints.FeedbackMessage == "switch to session \"Saved session\"":
                        Assert.False(allowSavedPayload.Task.IsCompleted);
                        Send(app, page, "produce fresh usage");
                        stage++;
                        break;
                    case 2 when host.Prompts.ContainsKey("session_2") && page.Header.ContextTokens == 4_321
                                && !page.Hints.IsRunning:
                        Assert.False(page.Header.ContextIsEstimated);
                        allowSavedPayload.TrySetResult();
                        stage++;
                        break;
                    case 3 when savedPayloadReturned.Task.IsCompleted:
                        if (++settledTicks < 3) break;
                        Assert.Equal(4_321, page.Header.ContextTokens);
                        Assert.False(page.Header.ContextIsEstimated);
                        Assert.Contains(page.Transcript.Lines, row => row.Text.Contains("produce fresh usage", StringComparison.Ordinal));
                        Send(app, page, "/exit");
                        stage++;
                        return false;
                }
                return true;
            });
        }
        finally
        {
            allowSavedPayload.TrySetResult();
        }

        Assert.Equal(4, stage);
        Assert.Single(host.Prompts);
    }

    private static HttpResponseMessage UsagePayload(string run, string payload, int input) => SingleHost.Json(new
    {
        run_id = run, payload_id = payload, kind = "modelusage", encoding = "json",
        content = JsonSerializer.Serialize(new { input_tokens = input, output_tokens = 50, total_tokens = input + 50 }),
    });

    private static HttpResponseMessage UsageEventStream(string run, string payload, int input, bool completed)
    {
        var text = new StringBuilder();
        void Frame(int sequence, string type, string? payloadRef = null) =>
            text.Append($"id: {sequence}\ndata: ").Append(JsonSerializer.Serialize(new
            {
                protocol_version = 1, event_id = "evt_" + sequence, run_id = run, session_id = "session_2", sequence,
                event_type = type, occurred_at = "2026-10-30T00:00:01Z", status = type == "run.state_changed" ? "completed" : "running",
                safe_summary = "reported", model_step_id = "step_" + new string('a', 32), payload_ref = payloadRef,
                input_tokens = input, output_tokens = 50, total_tokens = input + 50,
            })).Append("\n\n");
        Frame(1, "model.usage_recorded", payload);
        if (completed) Frame(2, "run.state_changed");
        return new(HttpStatusCode.OK) { Content = new StringContent(text.ToString(), Encoding.UTF8, "text/event-stream") };
    }
}
