using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Persistence;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Task 9 phase 3: deterministic headless coding loop over the real host runtime with a
/// scripted model, plus multi-client approval/cursor/cancel semantics. Spawns real
/// rg/git processes, so it runs non-parallel.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class HeadlessCodingLoopTests
{
    [Fact]
    public async Task Deterministic_headless_loop_inspects_edits_runs_and_stops_at_terminal_approval()
    {
        using var workspace = GitWorkspace.Create();
        var runner = new ScriptedRunner();
        runner.Enqueue(ToolCall("files.list", """{"path":"."}"""));
        runner.Enqueue(ToolCall("files.edit", $$"""{"path":"seed.txt","old_string":"seed","new_string":"repaired"}"""));
        runner.Enqueue(ToolCall("terminal.run", """{"executable":"rg","arguments":["--files"]}"""));
        runner.Enqueue(ToolCall("terminal.run", """{"executable":"git","arguments":["status","--porcelain=v2"]}"""));
        runner.Enqueue(ToolCall("terminal.run", """{"executable":"unknown-tool-xyz.exe","arguments":["--query-approval-state"]}"""));

        using var factory = new BackendWebApplicationFactory(dataPaths: workspace.DataPaths, modelStepRunner: runner, withInProcessBroker: true);
        using var client = factory.CreateAuthenticatedClient();
        var runId = await CreateTrustedRunAsync(client, workspace.Path);

        string lastStatus = string.Empty;
        var approvalSeen = await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30), s => lastStatus = s);
        Assert.True(approvalSeen, $"never reached waiting_approval; last={lastStatus}");

        var events = await ReadEventsAsync(client, runId);
        var completed = events.Where(e => e.GetProperty("event_type").GetString() == "tool.execution_completed").ToList();
        Assert.True(completed.Count >= 3, $"expected at least three completed executions, saw {completed.Count}");
        var preApprovalCompletions = completed.Count;
        Assert.Contains(events, e => e.GetProperty("event_type").GetString() == "approval.requested");

        var approvalId = events.Last(e => e.GetProperty("event_type").GetString() == "approval.requested").GetProperty("approval_id").GetString();

        // The unrelated dirty file must still be dirty after the read-only steps.
        Assert.Contains("M other.txt", workspace.GitStatusPorcelain(), StringComparison.Ordinal);

        // Approval resumes the parked call instead of dropping it: the unknown
        // executable executes exactly once and fails. The failed receipt stays
        // visible behind the recovery note, so the prose is answered with note #1,
        // the corrected read succeeds, and only the prose after that verified
        // call is accepted as the final answer.
        runner.Enqueue(FinalStep("repair finished"));
        runner.Enqueue(ToolCall("files.read", """{"path":"seed.txt"}"""));
        runner.Enqueue(FinalStep("repair finished"));
        using var approve = await client.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/approvals/{approvalId}",
            new { command = "allow_once" });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var completedSeen = await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, TimeSpan.FromSeconds(30));
        Assert.True(completedSeen, $"no terminal after approval; now={await GetStatusAsync(client, runId)}");
        var terminal = await ReadEventsAsync(client, runId);
        // The parked unknown executable plus the corrected read both complete:
        // the failed call is not counted as verified and the recovery read is.
        Assert.Equal(
            preApprovalCompletions + 2,
            terminal.Where(e => e.GetProperty("event_type").GetString() == "tool.execution_completed").Count());
        Assert.Contains(terminal, e => e.GetProperty("event_type").GetString() == "run.note_added");
        Assert.Equal("completed", await GetStatusAsync(client, runId));

        // The raw post-approval commit must not have advanced HEAD (nothing was staged),
        // and the unrelated dirty file is untouched.
        Assert.Equal(workspace.HeadAfterSeed, workspace.HeadNow());
        Assert.Contains("M other.txt", workspace.GitStatusPorcelain(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_edit_hint_reaches_the_model_context()
    {
        using var workspace = GitWorkspace.Create();
        var runner = new ScriptedRunner();
        runner.Enqueue(ToolCall(
            "files.edit",
            """{"path":"seed.txt","old_string":"text-not-in-the-file","new_string":"replacement"}"""));

        using var factory = new BackendWebApplicationFactory(
            dataPaths: workspace.DataPaths,
            modelStepRunner: runner,
            withInProcessBroker: true);
        using var client = factory.CreateAuthenticatedClient();
        var runId = await CreateTrustedRunAsync(client, workspace.Path);

        Assert.True(
            await WaitForStatusAsync(
                client,
                runId,
                new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" },
                TimeSpan.FromSeconds(30)),
            "run never reached a terminal state");

        var payloadStore = factory.Services.GetRequiredService<HarnessPayloadStore>();
        var taskRunId = new RunId(runId);
        var payloadIds = await payloadStore.ListPayloadIdsAsync(
            taskRunId,
            HarnessPayloadKind.ToolOutput,
            CancellationToken.None);
        string? failedOutput = null;
        foreach (var payloadId in payloadIds)
        {
            var payload = await payloadStore.ReadForModelAsync(
                taskRunId,
                payloadId,
                CancellationToken.None);
            var text = Encoding.UTF8.GetString(payload.Plaintext.Span);
            if (text.Contains("\"edit_no_match\"", StringComparison.Ordinal))
            {
                failedOutput = text;
                break;
            }
        }

        Assert.NotNull(failedOutput);
        var content = JsonNode.Parse(failedOutput!)!.AsObject();
        Assert.Equal("edit_no_match", content["error"]!.GetValue<string>());
        Assert.Contains(
            "old_string was not found in the file",
            content["hint"]!.GetValue<string>(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_failed_edit_cycle_stops_before_a_third_broker_traversal()
    {
        using var workspace = GitWorkspace.Create();
        var runner = new ScriptedRunner();
        var failedA = ToolCall(
            "files.edit",
            """{"path":"seed.txt","old_string":"missing-a","new_string":"replacement-a"}""");
        var failedB = ToolCall(
            "files.edit",
            """{"path":"seed.txt","old_string":"missing-b","new_string":"replacement-b"}""");
        runner.Enqueue(failedA);
        runner.Enqueue(failedB);
        runner.Enqueue(failedA);
        runner.Enqueue(failedB);
        runner.Enqueue(failedA);
        runner.Enqueue(failedB);
        runner.Enqueue(failedB);
        runner.Enqueue(failedB);

        using var factory = new BackendWebApplicationFactory(
            dataPaths: workspace.DataPaths,
            modelStepRunner: runner,
            withInProcessBroker: true);
        using var client = factory.CreateAuthenticatedClient();
        var runId = await CreateTrustedRunAsync(client, workspace.Path);

        Assert.True(
            await WaitForStatusAsync(
                client,
                runId,
                new HashSet<string>(StringComparer.Ordinal) { "waiting_user", "completed", "failed" },
                TimeSpan.FromSeconds(30)),
            "run did not reach a terminal or continue state");

        var events = await ReadEventsAsync(client, runId);
        Assert.Equal("waiting_user", await GetStatusAsync(client, runId));
        Assert.Equal(5, events.Count(item => item.GetProperty("event_type").GetString() == "tool.execution_completed"));
        Assert.Equal(5, events.Count(item => item.GetProperty("event_type").GetString() == "tool.proposed"));
        Assert.Equal(2, events.Count(item => item.GetProperty("event_type").GetString() == "run.note_added"));
        Assert.Equal("seed", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "seed.txt")));
    }

    [Fact]
    public async Task Approval_resume_executes_parked_proposal_without_reproposal()
    {
        using var workspace = GitWorkspace.Create();
        var outside = Path.Combine(Path.GetTempPath(), "ru-resume-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "outside");
        try
        {
            var runner = new ScriptedRunner();
            runner.Enqueue(ToolCall("files.read", $$"""{"path":{{JsonSerializer.Serialize(outside)}}}"""));

            using var factory = new BackendWebApplicationFactory(dataPaths: workspace.DataPaths, modelStepRunner: runner, withInProcessBroker: true);
            using var client = factory.CreateAuthenticatedClient();
            var runId = await CreateTrustedRunAsync(client, workspace.Path);

            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
                "run never reached waiting_approval");

            var events = await ReadEventsAsync(client, runId);
            var approvalId = events.Last(e => e.GetProperty("event_type").GetString() == "approval.requested").GetProperty("approval_id").GetString();

            runner.Enqueue(FinalStep("resumed done"));
            using var approve = await client.PostAsJsonAsync(
                $"/agent/v1/runs/{runId}/approvals/{approvalId}",
                new { command = "allow_once" });
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

            Assert.True(
                await WaitForStatusAsync(client, runId, new HashSet<string>(StringComparer.Ordinal) { "completed", "failed" }, TimeSpan.FromSeconds(30)),
                $"no terminal after approval; now={await GetStatusAsync(client, runId)}");

            var final = await ReadEventsAsync(client, runId);
            // One proposal only: approve resumes the parked call instead of the
            // model proposing the same call again with a new identity.
            Assert.Single(final, e => e.GetProperty("event_type").GetString() == "tool.proposed");
            Assert.Single(final, e => e.GetProperty("event_type").GetString() == "tool.execution_completed");
        }
        finally
        {
            try { File.Delete(outside); } catch { }
        }
    }

    [Fact]
    public async Task Multi_client_first_approval_wins_and_cancel_is_idempotent()
    {
        using var workspace = GitWorkspace.Create();
        var runner = new ScriptedRunner();
        runner.Enqueue(ToolCall("terminal.run", """{"executable":"unknown-tool-xyz.exe","arguments":["--query-approval-state"]}"""));

        using var factory = new BackendWebApplicationFactory(dataPaths: workspace.DataPaths, modelStepRunner: runner, withInProcessBroker: true);
        using var desktopClient = factory.CreateAuthenticatedClient();
        using var cliClient = factory.CreateAuthenticatedClient();
        var runId = await CreateTrustedRunAsync(desktopClient, workspace.Path);

        Assert.True(
            await WaitForStatusAsync(desktopClient, runId, new HashSet<string>(StringComparer.Ordinal) { "waiting_approval" }, TimeSpan.FromSeconds(30)),
            "run never reached waiting_approval");

        var events = await ReadEventsAsync(desktopClient, runId);
        var approvalRequested = events.Last(e => e.GetProperty("event_type").GetString() == "approval.requested");
        var approvalId = approvalRequested.GetProperty("approval_id").GetString();

        using var first = await desktopClient.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/approvals/{approvalId}", new { command = "allow_once" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await cliClient.PostAsJsonAsync(
            $"/agent/v1/runs/{runId}/approvals/{approvalId}", new { command = "allow_once" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var secondJson = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approval_stale", secondJson.GetProperty("error_code").GetString());

        // Independent reconnect cursors: each client replays only what follows its own
        // cursor position.
        var fullFeed = await ReadEventsAsync(desktopClient, runId);
        Assert.NotEmpty(fullFeed);
        var lastSequence = fullFeed.Max(e => e.GetProperty("sequence").GetInt64());
        var tailFeed = await ReadEventsAsync(cliClient, runId, afterSequence: lastSequence);
        Assert.All(tailFeed, e => Assert.True(e.GetProperty("sequence").GetInt64() > lastSequence));

        // One cancel is idempotent for every attached client.
        using var cancelA = await desktopClient.PostAsJsonAsync($"/agent/v1/runs/{runId}/cancel", new { });
        using var cancelB = await cliClient.PostAsJsonAsync($"/agent/v1/runs/{runId}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, cancelA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cancelB.StatusCode);
        Assert.Equal("cancelled", await GetStatusAsync(desktopClient, runId));
    }

    // ------------------------------------------------------------------
    // Fixtures and helpers.
    // ------------------------------------------------------------------

    private sealed class ScriptedRunner : IModelStepRunner
    {
        private readonly Queue<AssistantStep> _steps = new();

        public void Enqueue(AssistantStep step) => _steps.Enqueue(step);

        public Task<ModelStepResult> RunStepAsync(
            ModelStepRequest request,
            Func<AssistantDelta, ValueTask> publishDelta,
            CancellationToken cancellationToken)
        {
            // Deterministic fallback: any extra driver iteration ends the run instead of
            // throwing and leaving it stuck in Running.
            var step = _steps.Count > 0
                ? _steps.Dequeue()
                : FinalStep("script exhausted; done");
            return Task.FromResult(new ModelStepResult(
                step,
                RuAgent.Harness.Core.Prompting.SystemPromptComposer
                    .Compose(request.PromptContext, nativeToolCalling: true)
                    .Receipt));
        }
    }

    private static AssistantStep ToolCall(string toolId, string argumentsJson)
    {
        var (mappedId, mappedJson) = MapToPi(toolId, argumentsJson);
        return new(
            null,
            new[]
            {
                new ModelToolCall(
                    "provider_call_1",
                    mappedId,
                    "1.0.0",
                    JsonDocument.Parse(mappedJson).RootElement.Clone()),
            },
            AssistantFinishReason.ToolCalls,
            0);
    }

    private static (string Id, string Json) MapToPi(string toolId, string argumentsJson)
    {
        var node = JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        var id = toolId;
        switch (toolId)
        {
            case "files.list": id = "list"; node.Remove("op"); break;
            case "files.glob": id = "glob"; node.Remove("op"); break;
            case "files.search": id = "grep"; node.Remove("op"); break;
            case "files.read": id = "read"; node.Remove("op"); break;
            case "files.write": id = "write"; node["op"] = "write"; break;
            case "files.edit": id = "edit"; break;
            case "files.delete": id = "write"; node["op"] = "delete"; break;
            case "files.restore": id = "write"; node["op"] = "restore"; break;
            case "terminal.run": id = "bash"; node["mode"] = "run"; break;
            case "terminal.shell": id = "bash"; node["mode"] = "shell"; break;
            case "web.search": id = "web_search"; node.Remove("op"); break;
            case "web.fetch": id = "web_fetch"; node.Remove("op"); break;
            case "help.docs": id = "help"; node.Remove("op"); break;
        }
        return (id, node.ToJsonString());
    }

    private static AssistantStep FinalStep(string text) =>
        new(text, Array.Empty<ModelToolCall>(), AssistantFinishReason.Stop, 0);

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        using var resp = await client.PostAsync("/sessions/new", null);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("session_id").GetString()!;
    }

    private static async Task<string> CreateTrustedRunAsync(HttpClient client, string workspacePath)
    {
        var sessionId = await CreateSessionAsync(client);
        var body = new
        {
            session_id = sessionId,
            workspace_path = workspacePath,
            prompt = "headless tracer",
            preset = "standard",
            idempotency_key = Guid.NewGuid().ToString("N"),
        };
        using var resp = await client.PostAsJsonAsync("/agent/v1/runs", body);
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var runId = created.GetProperty("run_id").GetString()!;

        var trustPayload = new
        {
            trust_request_id = created.GetProperty("trust_request_id").GetString(),
            display_identity_hash = created.GetProperty("display_identity_hash").GetString(),
            decision = "approve",
        };
        using var trustResp = await client.PostAsJsonAsync($"/agent/v1/runs/{runId}/trust", trustPayload);
        trustResp.EnsureSuccessStatusCode();
        return runId;
    }

    private static async Task<bool> WaitForStatusAsync(HttpClient client, string runId, IReadOnlySet<string> states, TimeSpan timeout, System.Action<string>? trace = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200).ConfigureAwait(false);
            var current = await GetStatusAsync(client, runId).ConfigureAwait(false);
            trace?.Invoke(current);
            if (states.Contains(current))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string> GetStatusAsync(HttpClient client, string runId)
    {
        using var resp = await client.GetAsync($"/agent/v1/runs/{runId}");
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("status").GetString()!;
    }

    private static async Task<List<JsonElement>> ReadEventsAsync(HttpClient client, string runId, long? afterSequence = null)
    {
        var url = $"/agent/v1/runs/{runId}/events?live=0";
        if (afterSequence is { } after)
        {
            url += $"&after_sequence={after}";
        }

        using var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var events = new List<JsonElement>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data: ".Length..];
            if (payload.Contains("\"event_type\"", StringComparison.Ordinal))
            {
                events.Add(JsonDocument.Parse(payload).RootElement.Clone());
            }
        }

        return events;
    }

    private sealed class GitWorkspace : IDisposable
    {
        public string Path { get; }
        public RuBackend.Business.DataPaths DataPaths { get; }
        public string HeadAfterSeed { get; }
        private readonly string _root;

        private GitWorkspace(string root, string wsPath, RuBackend.Business.DataPaths dataPaths, string head)
        {
            _root = root;
            Path = wsPath;
            DataPaths = dataPaths;
            HeadAfterSeed = head;
        }

        public static GitWorkspace Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ru-headless-test-" + Guid.NewGuid().ToString("N"));
            var ws = System.IO.Path.Combine(root, "ws");
            var dataRoot = System.IO.Path.Combine(root, "data");
            Directory.CreateDirectory(ws);
            Directory.CreateDirectory(dataRoot);

            RunGit(ws, "init");
            RunGit(ws, "config", "user.email", "test@example.com");
            RunGit(ws, "config", "user.name", "Test User");
            File.WriteAllText(System.IO.Path.Combine(ws, "seed.txt"), "seed");
            File.WriteAllText(System.IO.Path.Combine(ws, "other.txt"), "dirty");
            RunGit(ws, "add", "seed.txt", "other.txt");
            RunGit(ws, "commit", "-m", "seed");
            File.AppendAllText(System.IO.Path.Combine(ws, "other.txt"), Environment.NewLine + "appended");

            var head = RunGit(ws, "rev-parse", "HEAD").Trim();
            var dataPaths = RuBackend.Business.DataPaths.Resolve(root, root, root, dataRoot, System.IO.Path.Combine(root, "logs"));
            return new GitWorkspace(root, ws, dataPaths, head);
        }

        public string GitStatusPorcelain()
        {
            return RunGit(Path, "status", "--porcelain").Trim();
        }

        public string HeadNow()
        {
            return RunGit(Path, "rev-parse", "HEAD").Trim();
        }

        public void Dispose()
        {
            try { SqliteTestPools.ClearHarnessDatabase(DataPaths.AgentHarnessDatabase); } catch { }
            try { Directory.Delete(_root, true); } catch { }
        }

        private static string RunGit(string cwd, params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(start)!;
            p.WaitForExit();
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"git {string.Join(" ", args)} failed: {stderr}");
            }

            return stdout;
        }
    }
}
