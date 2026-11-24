using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Privacy;
using RuBackend.Conversation;
using RuBackend.Harness;
using Xunit;
using Xunit.Abstractions;

namespace RuBackend.Tests.Harness;

public sealed class AgentActivityReplayBudgetTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Reasoning_payload_reads_are_limited_to_the_visible_activity_tail()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await fixture.SeedRunAsync();
        var history = new ReplayHistory();
        for (var index = 0; index < 1000; index++) history.AddThinking(index);
        await history.CommitAsync(fixture);

        var (body, reads) = await ReplayAsync(fixture);
        var rows = body.GetProperty("activities").EnumerateArray().ToArray();

        Assert.Equal(500, rows.Length);
        Assert.Equal("reasoning 500", rows[0].GetProperty("detail").GetString());
        Assert.Equal("reasoning 999", rows[^1].GetProperty("detail").GetString());
        Assert.Equal(500, reads);
    }

    [Fact]
    public async Task Old_non_todo_tool_outputs_are_not_decrypted_after_the_tail_is_full()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await fixture.SeedRunAsync();
        var history = new ReplayHistory();
        for (var index = 0; index < 1000; index++) history.AddTool(index, "read_file",
            JsonSerializer.Serialize(new { path = $"file-{index}.txt" }),
            JsonSerializer.Serialize(new { content = new string('x', 4096) }));
        await history.CommitAsync(fixture);

        var (body, reads) = await ReplayAsync(fixture);
        var rows = body.GetProperty("activities").EnumerateArray().ToArray();

        Assert.Equal(500, rows.Length);
        Assert.Equal("act_" + 501.ToString("x32"), rows[0].GetProperty("activity_id").GetString());
        Assert.Equal("act_" + 1000.ToString("x32"), rows[^1].GetProperty("activity_id").GetString());
        // Older arguments are needed to find a possible checklist. Their large
        // results cannot contribute to either the visible tail or a checklist.
        Assert.Equal(1000 + 500, reads);
    }

    [Fact]
    public async Task A_recent_checklist_stops_reads_before_older_proposals_outside_the_tail()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await fixture.SeedRunAsync();
        var history = new ReplayHistory();
        for (var index = 0; index < 1000; index++) history.AddTool(index, "read_file",
            JsonSerializer.Serialize(new { path = $"file-{index}.txt" }), "{}");
        history.AddTool(2000, "todo_write", "{\"todos\":[]}", "{\"todos\":[]}");
        await history.CommitAsync(fixture);

        var (body, reads) = await ReplayAsync(fixture);
        var rows = body.GetProperty("activities").EnumerateArray().ToArray();
        Assert.Equal(500, rows.Length);
        Assert.Equal("act_" + 503.ToString("x32"), rows[0].GetProperty("activity_id").GetString());
        Assert.Empty(rows[^1].GetProperty("todos").EnumerateArray());
        Assert.Equal(998, reads);
    }

    [Theory]
    [InlineData("old", 499)]
    [InlineData("old", 600)]
    [InlineData("clear", 600)]
    [InlineData("malformed", 600)]
    [InlineData("unreadable", 600)]
    public async Task Latest_valid_checklist_survives_outside_the_activity_tail(string update, int thinkingRows)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await fixture.SeedRunAsync();
        var history = new ReplayHistory();
        const string valid = "{\"todos\":[{\"content\":\"Retain the older task\",\"status\":\"in_progress\"}]}";
        history.AddTool(2000, "todo_write", valid, valid);
        if (update != "old") history.AddTool(2001, "todo_write", "{\"todos\":[]}",
            update == "malformed" ? "{\"todos\":\"damaged\"}" : "{\"todos\":[]}");
        for (var index = 0; index < thinkingRows; index++) history.AddThinking(index);
        await history.CommitAsync(fixture);
        if (update == "unreadable") await CorruptLatestToolResultAsync(fixture);

        var (body, reads) = await ReplayAsync(fixture);
        var rows = body.GetProperty("activities").EnumerateArray().ToArray();
        Assert.Equal(500, rows.Length);
        var checklist = Assert.Single(rows, row => row.TryGetProperty("todos", out var items)
            && items.ValueKind == JsonValueKind.Array);
        var todos = checklist.GetProperty("todos").EnumerateArray().ToArray();
        if (update == "clear") Assert.Empty(todos);
        else Assert.Equal("Retain the older task", Assert.Single(todos).GetProperty("content").GetString());
        Assert.Equal($"reasoning {thinkingRows - 1}", rows[^1].GetProperty("detail").GetString());
        Assert.Equal(Math.Min(500, thinkingRows) + (update is "malformed" or "unreadable" ? 4 : 2), reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unfinished_calls_keep_live_or_interrupted_state_with_bounded_argument_reads(bool terminal)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await fixture.SeedRunAsync();
        var history = new ReplayHistory();
        for (var index = 0; index < 600; index++) history.AddTool(index, "read_file",
            JsonSerializer.Serialize(new { path = $"pending-{index}.txt" }), "{}", pending: true);
        await history.CommitAsync(fixture, terminal);

        var (body, reads) = await ReplayAsync(fixture);
        var rows = body.GetProperty("activities").EnumerateArray().ToArray();
        Assert.Equal(500, rows.Length);
        Assert.All(rows, row => Assert.Equal(terminal ? "failed" : "started", row.GetProperty("state").GetString()));
        Assert.Equal("act_" + 101.ToString("x32"), rows[0].GetProperty("activity_id").GetString());
        Assert.Equal(500, reads);
    }

    [Fact]
    public async Task Unreadable_latest_tool_result_backfills_the_tail_from_earlier_valid_calls()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await fixture.SeedRunAsync();
        var history = new ReplayHistory();
        for (var index = 0; index < 501; index++) history.AddTool(index, "read_file",
            JsonSerializer.Serialize(new { path = $"valid-{index}.txt" }), "{}");
        await history.CommitAsync(fixture);
        await CorruptLatestToolResultAsync(fixture);

        var (body, reads) = await ReplayAsync(fixture);
        var rows = body.GetProperty("activities").EnumerateArray().ToArray();
        Assert.Equal(500, rows.Length);
        Assert.Equal("act_" + 1.ToString("x32"), rows[0].GetProperty("activity_id").GetString());
        Assert.Equal("act_" + 500.ToString("x32"), rows[^1].GetProperty("activity_id").GetString());
        Assert.Equal(1002, reads);
    }

    [Fact]
    public async Task Private_reasoning_never_enters_replay_and_does_not_remove_its_thinking_row()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await fixture.SeedRunAsync();
        var history = new ReplayHistory();
        PayloadId last = default;
        for (var index = 0; index < 1000; index++) last = history.AddThinking(index);
        await history.CommitAsync(fixture);
        await using (var connection = await fixture.Options.OpenConnectionAsync(default))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_payloads SET audience=4 WHERE payload_id=$payload";
            command.Parameters.AddWithValue("$payload", last.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var (body, reads) = await ReplayAsync(fixture);
        var rows = body.GetProperty("activities").EnumerateArray().ToArray();
        Assert.Equal(500, rows.Length);
        Assert.Equal("act_" + 1000.ToString("x32"), rows[^1].GetProperty("activity_id").GetString());
        Assert.True(!rows[^1].TryGetProperty("detail", out var detail) || detail.ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain("reasoning 999", body.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(499, reads);
    }

    private static async Task CorruptLatestToolResultAsync(BackendHarnessTestFixture fixture)
    {
        await using var connection = await fixture.Options.OpenConnectionAsync(default);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_payloads SET authentication_tag=zeroblob(16) WHERE payload_id=(SELECT payload_id FROM agent_events WHERE run_id=$run AND kind='tool.execution_completed' ORDER BY sequence DESC LIMIT 1)";
        command.Parameters.AddWithValue("$run", ReplayHistory.Run.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<(JsonElement Body, int Reads)> ReplayAsync(BackendHarnessTestFixture fixture)
    {
        var keys = new CountingKeyStore(fixture.KeyStore);
        var payloads = new HarnessPayloadStore(fixture.Options, new HarnessPayloadCryptography(keys), KnownSecretRedactor.Empty);
        var service = new AgentActivitiesService(fixture.EventStore, payloads, new MetadataPolicy());
        var clock = Stopwatch.StartNew();
        var result = await service.ListRunActivitiesAsync(ReplayHistory.Run.Value, default);
        Assert.Equal(200, result.StatusCode);
        output.WriteLine($"Replay: {keys.Reads} payload decryptions, {clock.Elapsed.TotalMilliseconds:F1} ms");
        return (JsonSerializer.SerializeToElement(result.Body), keys.Reads);
    }

    private sealed class CountingKeyStore(IHarnessKeyStore inner) : IHarnessKeyStore
    {
        internal int Reads;
        public ValueTask<byte[]> GetOrCreateKeyAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            return inner.GetOrCreateKeyAsync(ct);
        }
    }

    private sealed class ReplayHistory
    {
        internal static readonly RunId Run = new("run_11111111111111111111111111111111");
        private readonly List<HarnessEventIntent> _events = [];
        private readonly List<HarnessPayloadIntent> _payloads = [];

        internal PayloadId AddThinking(int index)
        {
            var step = new ModelStepId("step_" + (index + 1).ToString("x32"));
            var payload = AddPayload(HarnessPayloadKind.ModelReasoning, $"reasoning {index}", json: false);
            _events.Add(new(NewEvent(), HarnessEventKind.ModelStepStarted, modelStepId: step));
            _events.Add(new(NewEvent(), HarnessEventKind.ModelReasoningRecorded, modelStepId: step, payloadId: payload));
            _events.Add(new(NewEvent(), HarnessEventKind.ModelStepCompleted, modelStepId: step));
            return payload;
        }

        internal void AddTool(int index, string tool, string arguments, string result, bool pending = false)
        {
            var call = new ToolCallId("call_" + (index + 1).ToString("x32"));
            var step = new ModelStepId("step_" + (index + 1).ToString("x32"));
            var provider = $"provider_{index}";
            var args = AddPayload(HarnessPayloadKind.ToolArguments,
                $"{{\"tool_id\":\"{tool}\",\"tool_version\":\"1.0.0\",\"arguments\":{arguments}}}");
            _events.Add(new(NewEvent(), HarnessEventKind.ToolProposed, modelStepId: step,
                toolCallId: call, providerCallId: provider, payloadId: args, safeCount: 1));
            if (pending) return;
            var receipt = AddPayload(HarnessPayloadKind.ToolOutput, result);
            _events.Add(new(NewEvent(), HarnessEventKind.ToolExecutionCompleted, modelStepId: step,
                toolCallId: call, providerCallId: provider, executionId: new ExecutionId("exec_" + (index + 1).ToString("x32")),
                payloadId: receipt, toolOutcome: ToolExecutionOutcome.Succeeded, sideEffectsPossible: false));
        }

        private PayloadId AddPayload(HarnessPayloadKind kind, string content, bool json = true)
        {
            var id = new PayloadId("payload_" + Guid.NewGuid().ToString("N"));
            var bytes = Encoding.UTF8.GetBytes(content);
            _payloads.Add(new(id, kind, kind == HarnessPayloadKind.ModelReasoning
                ? HarnessPayloadAudience.AuthenticatedClient
                : HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                json ? HarnessPayloadEncoding.Json : HarnessPayloadEncoding.Utf8Text, bytes.Length, bytes));
            return id;
        }

        internal async Task CommitAsync(BackendHarnessTestFixture fixture, bool terminal = true)
        {
            var run = (await fixture.EventStore.GetRunAsync(Run, default))!;
            var now = BackendHarnessTestFixture.Timestamp.AddMinutes(1);
            var next = new AgentRun(run.RunId, run.SessionId, run.WorkspaceId,
                terminal ? new(HarnessRunState.Completed) : run.Status, run.Version + 1,
                run.LastSequence + _events.Count, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow,
                run.Preset, run.PolicySnapshotVersion, run.PolicySnapshotSha256, run.CreatedAt, now,
                terminalAt: terminal ? now : null, permissionProfileVersion: run.PermissionProfileVersion);
            Assert.Equal(HarnessCommitOutcome.Committed, (await fixture.EventStore.CommitAsync(
                new(Run, run.Version, next, _events, _payloads, []), default)).Outcome);
        }

        private static EventId NewEvent() => new("evt_" + Guid.NewGuid().ToString("N"));
    }
}
