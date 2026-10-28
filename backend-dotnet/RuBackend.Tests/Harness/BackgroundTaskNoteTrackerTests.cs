using System.Text.Json.Nodes;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class BackgroundTaskNoteTrackerTests
{
    [Fact]
    public async Task Advice_uses_the_current_terminal_lifecycle_tools()
    {
        var tracker = new BackgroundTaskNoteTracker(_ => Task.FromResult(Payload(("t1", "running", null))));
        var note = await tracker.ProbeAsync(default);
        Assert.Contains("terminal_read", note);
        Assert.Contains("terminal_stop", note);
        Assert.DoesNotContain("bash", note);
    }

    private static JsonObject Payload(params (string Id, string State, int? Exit)[] tasks)
    {
        var array = new JsonArray();
        foreach (var (id, state, exit) in tasks)
        {
            array.Add(new JsonObject
            {
                ["execution_id"] = id,
                ["state"] = state,
                ["exit_code"] = exit,
            });
        }

        return new JsonObject { ["status"] = "succeeded", ["tasks"] = array };
    }

    [Fact]
    public async Task Reports_new_tasks_once_and_state_changes_once()
    {
        var payloads = new Queue<JsonObject>(
        [
            Payload(("t1", "running", null)),
            Payload(("t1", "running", null)),
            Payload(("t1", "exited", 0)),
        ]);
        var tracker = new BackgroundTaskNoteTracker(_ => Task.FromResult(payloads.Dequeue()));

        var first = await tracker.ProbeAsync(CancellationToken.None);
        Assert.NotNull(first);
        Assert.Contains("t1: running", first, StringComparison.Ordinal);

        var second = await tracker.ProbeAsync(CancellationToken.None);
        Assert.Null(second);

        var third = await tracker.ProbeAsync(CancellationToken.None);
        Assert.NotNull(third);
        Assert.Contains("t1: running -> exited, exit 0", third, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_payload_and_probe_exception_stay_silent()
    {
        var failed = new BackgroundTaskNoteTracker(
            _ => Task.FromResult(new JsonObject
            {
                ["status"] = "failed",
                ["error_code"] = "broker_gateway_unavailable",
            }));
        Assert.Null(await failed.ProbeAsync(CancellationToken.None));

        var throwing = new BackgroundTaskNoteTracker(_ => throw new InvalidOperationException("down"));
        Assert.Null(await throwing.ProbeAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Forgets_ids_the_broker_no_longer_lists()
    {
        var payloads = new Queue<JsonObject>(
        [
            Payload(("t1", "exited", 0)),
            Payload(),
            Payload(("t1", "exited", 0)),
        ]);
        var tracker = new BackgroundTaskNoteTracker(_ => Task.FromResult(payloads.Dequeue()));

        Assert.NotNull(await tracker.ProbeAsync(CancellationToken.None));
        Assert.Null(await tracker.ProbeAsync(CancellationToken.None));
        Assert.NotNull(await tracker.ProbeAsync(CancellationToken.None));
    }
}
