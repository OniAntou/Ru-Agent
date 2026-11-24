using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class TuiSingleSessionTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 100)]
    [InlineData(true, 100)]
    public async Task Saved_session_accepts_a_prompt_while_history_and_model_details_are_pending(bool startup, int completedRuns)
    {
        var runIds = Enumerable.Range(1, completedRuns).Select(index => $"run_{index:x32}").ToArray();
        var runId = runIds[^1];
        var runPath = $"/agent/v1/runs/{runId}";
        var terminalPaths = runIds.Select(id => $"/agent/v1/runs/{id}").ToHashSet(StringComparer.Ordinal);
        var sessionRequested = NewSignal();
        var detailsRequested = NewSignal();
        var activityRequested = NewSignal();
        var allowDetails = NewSignal();
        var allowActivity = NewSignal();
        var terminalReads = 0;
        var runListReads = 0;
        using var host = new SingleHost
        {
            ReadTurnPage = true,
            SavedMessages = SavedTurn(runId),
            ReplayResponse = async (path, ct) =>
            {
                if (path == "/sessions/session_2/activate") sessionRequested.TrySetResult();
                if (path == "/agent/v1/sessions/session_2/runs") Interlocked.Increment(ref runListReads);
                if (terminalPaths.Contains(path)) Interlocked.Increment(ref terminalReads);
                if (path == runPath + "/activities")
                {
                    activityRequested.TrySetResult();
                    await allowActivity.Task.WaitAsync(ct);
                    return HistoryActivity("Earlier narration");
                }
                if (path == "/models" && sessionRequested.Task.IsCompleted)
                {
                    detailsRequested.TrySetResult();
                    await allowDetails.Task.WaitAsync(ct);
                }
                return null;
            },
        };
        foreach (var id in runIds) host.SeedCompletedRun(id, "session_2");

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
                case 1 when activityRequested.Task.IsCompleted && detailsRequested.Task.IsCompleted
                            && rows.Contains("Earlier answer"):
                    Assert.Single(rows, row => row.Contains("Earlier request", StringComparison.Ordinal));
                    Assert.Single(rows, row => row == "Earlier answer");
                    Assert.DoesNotContain("Earlier narration", rows);
                    Assert.Equal(0, Volatile.Read(ref terminalReads));
                    Assert.Equal(1, Volatile.Read(ref runListReads));
                    stage++;
                    break;
                case 2:
                    Send(app, page, "new request while details load");
                    Assert.Equal(string.Empty, page.Composer.Input.Text);
                    stage++;
                    break;
                case 3 when host.Streams.ContainsKey("session_2"):
                    Assert.Equal("new request while details load", host.Prompts["session_2"]);
                    Assert.Contains(rows, row => row.Contains("new request while details load", StringComparison.Ordinal));
                    Assert.Single(rows, row => row == "Earlier answer");
                    Assert.False(allowActivity.Task.IsCompleted);
                    Assert.False(allowDetails.Task.IsCompleted);
                    Assert.Equal(0, Volatile.Read(ref terminalReads));
                    Send(app, page, "/exit");
                    stage++;
                    return false;
            }
            return true;
        }, session: startup ? "session_2" : "");

        Assert.Equal(4, stage);
        Assert.Equal(startup ? 0 : 1, host.SessionListReads);
        Assert.Equal(1, runListReads);
        Assert.Equal(0, terminalReads);
    }

    [Fact]
    public async Task A_hundred_terminal_summaries_share_one_read_and_enrich_history_after_switch_is_ready()
    {
        var runIds = Enumerable.Range(1, 100).Select(index => $"run_{index:x32}").ToArray();
        var latestRun = runIds[^1];
        var terminalPaths = runIds.Select(id => $"/agent/v1/runs/{id}").ToHashSet(StringComparer.Ordinal);
        var activityRequested = NewSignal();
        var allowActivity = NewSignal();
        var terminalReads = 0;
        var runListReads = 0;
        using var host = new SingleHost
        {
            ReadTurnPage = true,
            SavedMessages = SavedTurn(latestRun),
            ReplayResponse = async (path, ct) =>
            {
                if (path == "/agent/v1/sessions/session_2/runs") Interlocked.Increment(ref runListReads);
                if (terminalPaths.Contains(path)) Interlocked.Increment(ref terminalReads);
                if (path != $"/agent/v1/runs/{latestRun}/activities") return null;
                activityRequested.TrySetResult();
                await allowActivity.Task.WaitAsync(ct);
                return HistoryActivity("Enriched saved narration");
            },
        };
        foreach (var run in runIds) host.SeedCompletedRun(run, "session_2");

        var stage = 0;
        await Run(host, (app, page) =>
        {
            var rows = page.Transcript.Lines.Select(row => row.Text).ToArray();
            switch (stage)
            {
                case 0:
                    Send(app, page, "/sessions id:session_2");
                    stage++;
                    break;
                case 1 when activityRequested.Task.IsCompleted
                            && page.Hints.FeedbackMessage == "switch to session \"Saved session\"":
                    Assert.Single(rows, row => row == "Earlier answer");
                    Assert.DoesNotContain("Enriched saved narration", rows);
                    Assert.Equal(1, Volatile.Read(ref runListReads));
                    Assert.Equal(0, Volatile.Read(ref terminalReads));
                    allowActivity.TrySetResult();
                    stage++;
                    break;
                case 2 when rows.Contains("Enriched saved narration"):
                    Assert.Single(rows, row => row.Contains("Earlier request", StringComparison.Ordinal));
                    Assert.Single(rows, row => row == "Earlier answer");
                    Assert.Single(rows, row => row == "Enriched saved narration");
                    Assert.Equal(1, Volatile.Read(ref runListReads));
                    Assert.Equal(0, Volatile.Read(ref terminalReads));
                    Assert.Empty(host.Prompts);
                    Send(app, page, "/exit");
                    stage++;
                    return false;
            }
            return true;
        });

        Assert.Equal(3, stage);
        Assert.Equal(1, runListReads);
        Assert.Equal(0, terminalReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_history_enrichment_cannot_erase_a_new_live_prompt(bool startup)
    {
        var runId = "run_" + new string('b', 32);
        var activityRequested = NewSignal();
        var allowActivity = NewSignal();
        var activityReturned = NewSignal();
        using var host = new SingleHost
        {
            ReadTurnPage = true,
            SavedMessages = SavedTurn(runId),
            ReplayResponse = async (path, _) =>
            {
                if (path != $"/agent/v1/runs/{runId}/activities") return null;
                activityRequested.TrySetResult();
                // Deliberately finish after cancellation to exercise stale-result ownership.
                await allowActivity.Task;
                activityReturned.TrySetResult();
                return HistoryActivity("Stale enriched narration");
            },
        };
        host.SeedCompletedRun(runId, "session_2");

        var stage = startup ? 1 : 0;
        var settledTicks = 0;
        try
        {
            await Run(host, (app, page) =>
            {
                var rows = page.Transcript.Lines.Select(row => row.Text).ToArray();
                switch (stage)
                {
                    case 0:
                        Send(app, page, "/sessions id:session_2");
                        stage++;
                        break;
                    case 1 when activityRequested.Task.IsCompleted && rows.Contains("Earlier answer"):
                        stage++;
                        break;
                    case 2:
                        Send(app, page, "keep this live prompt");
                        stage++;
                        break;
                    case 3 when host.Streams.ContainsKey("session_2"):
                        Assert.Equal("keep this live prompt", host.Prompts["session_2"]);
                        allowActivity.TrySetResult();
                        stage++;
                        break;
                    case 4 when activityReturned.Task.IsCompleted:
                        // Pump the UI after the delayed HTTP completion and its queued paint.
                        if (++settledTicks < 3) break;
                        Assert.Contains(rows, row => row.Contains("keep this live prompt", StringComparison.Ordinal));
                        Assert.Single(rows, row => row == "Earlier answer");
                        Assert.DoesNotContain("Stale enriched narration", rows);
                        Assert.True(page.Hints.IsRunning);
                        Send(app, page, "/exit");
                        stage++;
                        return false;
                }
                return true;
            }, session: startup ? "session_2" : "");
        }
        finally
        {
            allowActivity.TrySetResult();
        }

        Assert.Equal(5, stage);
        Assert.Single(host.Prompts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Navigating_away_invalidates_pending_history_enrichment(bool newSession)
    {
        var runId = "run_" + new string('c', 32);
        var targetSession = newSession ? "session_1" : "session_3";
        var activityRequested = NewSignal();
        var allowActivity = NewSignal();
        var activityReturned = NewSignal();
        using var host = new SingleHost
        {
            ReadTurnPage = true,
            SavedMessages = SavedTurn(runId),
            ReplayResponse = async (path, _) =>
            {
                if (path == $"/sessions/{targetSession}/activate")
                    return SingleHost.Json(new { status = "ok", session = new { id = targetSession, title = "Empty target", messages = Array.Empty<object>() } });
                if (path == $"/sessions/{targetSession}/messages")
                    return SingleHost.Json(new { status = "ok", messages = Array.Empty<object>() });
                if (path != $"/agent/v1/runs/{runId}/activities") return null;
                activityRequested.TrySetResult();
                await allowActivity.Task;
                activityReturned.TrySetResult();
                return HistoryActivity("Another session's stale narration");
            },
        };
        host.SeedCompletedRun(runId, "session_2");

        var stage = 0;
        var settledTicks = 0;
        try
        {
            await Run(host, (app, page) =>
            {
                var rows = page.Transcript.Lines.Select(row => row.Text).ToArray();
                switch (stage)
                {
                    case 0:
                        Send(app, page, "/sessions id:session_2");
                        stage++;
                        break;
                    case 1 when activityRequested.Task.IsCompleted
                                && page.Hints.FeedbackMessage == "switch to session \"Saved session\"":
                        Assert.Contains("Earlier answer", rows);
                        stage++;
                        break;
                    case 2:
                        Send(app, page, newSession ? "/new" : $"/sessions id:{targetSession}");
                        stage++;
                        break;
                    case 3 when page.Hints.FeedbackMessage == "switch to session \"Empty target\"":
                        Assert.True(page.Landing.Visible);
                        allowActivity.TrySetResult();
                        stage++;
                        break;
                    case 4 when activityReturned.Task.IsCompleted:
                        if (++settledTicks < 3) break;
                        Assert.True(page.Landing.Visible);
                        Assert.DoesNotContain("Earlier answer", rows);
                        Assert.DoesNotContain("Another session's stale narration", rows);
                        Assert.Equal(newSession ? 1 : 0, host.CreatedSessions);
                        Assert.Empty(host.Prompts);
                        Send(app, page, "/exit");
                        stage++;
                        return false;
                }
                return true;
            });
        }
        finally
        {
            allowActivity.TrySetResult();
        }

        Assert.Equal(5, stage);
        Assert.Empty(host.Cancels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opening_waits_for_run_classification_before_continue_can_follow_the_adopted_run(bool startup)
    {
        var runListRequested = NewSignal();
        var allowRunList = NewSignal();
        using var host = new SingleHost
        {
            ReadTurnPage = true,
            SavedMessages = [new { role = "user", content = "Earlier pending request" }],
            ReplayResponse = async (path, ct) =>
            {
                if (path == "/agent/v1/sessions/session_2/runs")
                {
                    runListRequested.TrySetResult();
                    await allowRunList.Task.WaitAsync(ct);
                }
                return null;
            },
        };
        var externalRun = host.SeedExternalRun("session_2");

        var stage = startup ? 1 : 0;
        var draftRetained = false;
        await Run(host, (app, page) =>
        {
            var rows = page.Transcript.Lines.Select(row => row.Text).ToArray();
            switch (stage)
            {
                case 0:
                    Send(app, page, "/sessions id:session_2");
                    stage++;
                    break;
                case 1 when runListRequested.Task.IsCompleted
                            && rows.Any(row => row.Contains("Earlier pending request", StringComparison.Ordinal)):
                    Send(app, page, "Continue");
                    draftRetained = page.Composer.Input.Text == "Continue";
                    if (startup) Assert.True(draftRetained);
                    Assert.Empty(host.Prompts);
                    Assert.Empty(host.Streams);
                    Assert.Equal(0, host.CreatedSessions);
                    allowRunList.TrySetResult();
                    stage++;
                    break;
                case 2 when rows.Any(row => row.Contains(externalRun, StringComparison.Ordinal)
                            && row.Contains("still running", StringComparison.Ordinal)):
                    if (draftRetained) Send(app, page, "Continue");
                    stage++;
                    break;
                case 3 when host.Streams.TryGetValue("session_2", out var attached):
                    Assert.Equal(externalRun, attached);
                    Assert.Empty(host.Prompts);
                    Assert.Equal(0, host.CreatedSessions);
                    Send(app, page, "/exit");
                    stage++;
                    return false;
            }
            return true;
        }, session: startup ? "session_2" : "");

        Assert.Equal(4, stage);
        Assert.Empty(host.Cancels);
    }

    private static object[] SavedTurn(string runId) =>
    [
        new { role = "user", content = "Earlier request", source_run_id = runId },
        new { role = "assistant", content = "Earlier answer", source_run_id = runId },
    ];

    private static System.Net.Http.HttpResponseMessage HistoryActivity(string text) => SingleHost.Json(new
    {
        activities = Array.Empty<object>(),
        transcript = new[] { new { kind = "text", sequence = 1, text } },
    });

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
