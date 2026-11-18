using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class TuiSingleSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saved_messages_are_visible_while_run_history_and_model_details_are_pending(bool startup)
    {
        var runId = "run_" + new string('a', 32);
        var runPath = $"/agent/v1/runs/{runId}";
        var sessionRequested = NewSignal();
        var usageRequested = NewSignal();
        var statusRequested = NewSignal();
        var activityRequested = NewSignal();
        var allowUsage = NewSignal();
        var allowStatus = NewSignal();
        var allowActivity = NewSignal();
        using var host = new SingleHost
        {
            ReadTurnPage = true,
            SavedMessages =
            [
                new { role = "user", content = "Earlier request", source_run_id = runId },
                new { role = "assistant", content = "Earlier answer", source_run_id = runId },
            ],
            ReplayResponse = async (path, ct) =>
            {
                if (path == "/sessions/session_2/activate") sessionRequested.TrySetResult();
                if (path == runPath)
                {
                    statusRequested.TrySetResult();
                    await allowStatus.Task.WaitAsync(ct);
                }
                if (path == runPath + "/activities")
                {
                    activityRequested.TrySetResult();
                    await allowActivity.Task.WaitAsync(ct);
                    return SingleHost.Json(new
                    {
                        activities = Array.Empty<object>(),
                        transcript = new[] { new { kind = "text", sequence = 1, text = "Earlier narration" } },
                    });
                }
                if (path == "/models" && sessionRequested.Task.IsCompleted)
                {
                    usageRequested.TrySetResult();
                    await allowUsage.Task.WaitAsync(ct);
                }
                return null;
            },
        };
        host.SeedCompletedRun(runId, "session_2");

        var stage = startup ? 1 : 0;
        await Run(host, (app, page) =>
        {
            var rows = page.Transcript.Lines.Select(row => row.Text).ToArray();
            switch (stage)
            {
                case 0:
                    Send(app, page, "/sessions id:session_2");
                    stage++;
                    break;
                case 1 when statusRequested.Task.IsCompleted && (startup || usageRequested.Task.IsCompleted)
                            && rows.Contains("Earlier answer"):
                    Assert.Single(rows, row => row.Contains("Earlier request", StringComparison.Ordinal));
                    Assert.Single(rows, row => row == "Earlier answer");
                    Assert.DoesNotContain("Earlier narration", rows);
                    if (startup)
                    {
                        Send(app, page, "draft during startup");
                        Assert.Equal("draft during startup", page.Composer.Input.Text);
                        Assert.Empty(host.Prompts);
                    }
                    allowStatus.TrySetResult();
                    stage++;
                    break;
                case 2 when activityRequested.Task.IsCompleted:
                    Assert.Single(rows, row => row == "Earlier answer");
                    Assert.DoesNotContain("Earlier narration", rows);
                    allowActivity.TrySetResult();
                    stage++;
                    break;
                case 3 when rows.Contains("Earlier narration") && usageRequested.Task.IsCompleted:
                    Assert.Single(rows, row => row.Contains("Earlier request", StringComparison.Ordinal));
                    Assert.Single(rows, row => row == "Earlier answer");
                    Assert.Equal(startup ? 0 : 1, Volatile.Read(ref host.SessionListReads));
                    allowUsage.TrySetResult();
                    stage++;
                    break;
                case 4 when startup || page.Hints.FeedbackMessage == "switch to session \"Saved session\"":
                    Send(app, page, "/exit");
                    stage++;
                    return false;
            }
            return true;
        }, session: startup ? "session_2" : "");

        Assert.Equal(5, stage);
        Assert.Equal(startup ? 0 : 1, host.SessionListReads);
        Assert.Empty(host.Prompts);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
