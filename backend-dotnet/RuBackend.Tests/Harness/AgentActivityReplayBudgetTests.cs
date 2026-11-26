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
        // The checklist leads the newest page as an extra row. Swapping it in for the oldest
        // row used to lose that row for good, which paging can no longer allow.
        Assert.Equal(501, rows.Length);
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

    /// <summary>
    /// Paging is the contract that replaces the old hard cut: walking every page from the
    /// newest back must yield each row exactly once, in order, the same rows one large page
    /// holds. The fixture puts a todo_write completion (two rows from one event) on a page
    /// boundary and an unreadable result in the middle, the two ways a cursor can drift.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public async Task Walking_every_page_back_yields_each_row_once_in_order(int limit)
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await fixture.SeedRunAsync();
        var history = new ReplayHistory();
        for (var index = 0; index < 10; index++) history.AddThinking(index);
        history.AddTool(100, "read_file", "{\"path\":\"a.txt\"}", "{}");
        history.AddTool(101, "todo_write", "{\"todos\":[]}", "{\"todos\":[]}");
        for (var index = 10; index < 16; index++) history.AddThinking(index);
        history.AddTool(102, "read_file", "{\"path\":\"b.txt\"}", "{}");
        for (var index = 16; index < 20; index++) history.AddThinking(index);
        await history.CommitAsync(fixture);
        await CorruptToolResultAsync(fixture, newest: false);

        // One unpinned page large enough for everything is the reference list.
        var (whole, _) = await ReplayAsync(fixture, before: long.MaxValue, limit: 500);
        var expected = Ids(whole);
        Assert.False(whole.GetProperty("has_more").GetBoolean());

        var pages = new List<string[]>();
        long? cursor = long.MaxValue;
        for (var guard = 0; cursor is not null; guard++)
        {
            Assert.True(guard < 100, "the cursor must move on every page");
            var (page, _) = await ReplayAsync(fixture, cursor, limit);
            var ids = Ids(page);
            Assert.InRange(ids.Length, 0, limit + 1);
            pages.Add(ids);
            var more = page.GetProperty("has_more").GetBoolean();
            var next = page.GetProperty("next_before_sequence");
            Assert.Equal(more, next.ValueKind == JsonValueKind.Number);
            if (more) Assert.True(next.GetInt64() < cursor);
            cursor = more ? next.GetInt64() : null;
        }

        var walked = pages.AsEnumerable().Reverse().SelectMany(ids => ids).ToArray();
        Assert.Equal(expected, walked);
    }

    /// <summary>
    /// Narration is read per page: each page carries the words between its own rows, every
    /// chunk lands on exactly one page, and a long run no longer loses its whole transcript
    /// because the run as a whole is over the narration budget.
    /// </summary>
    [Fact]
    public async Task Each_page_carries_only_the_narration_in_its_own_window()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await fixture.SeedRunAsync();
        var history = new ReplayHistory();
        for (var index = 0; index < 100; index++) history.AddThinking(index, narration: $"said {index}.");
        await history.CommitAsync(fixture);

        var (newest, _) = await ReplayAsync(fixture, before: null, limit: 40);
        var (middle, _) = await ReplayAsync(fixture, newest.GetProperty("next_before_sequence").GetInt64(), 40);
        var (oldest, _) = await ReplayAsync(fixture, middle.GetProperty("next_before_sequence").GetInt64(), 40);
        Assert.False(oldest.GetProperty("has_more").GetBoolean());

        static string[] Said(JsonElement body) => body.GetProperty("transcript").EnumerateArray()
            .Where(entry => entry.GetProperty("kind").GetString() == "text")
            .Select(entry => entry.GetProperty("text").GetString()!).ToArray();
        Assert.Equal(Enumerable.Range(60, 40).Select(index => $"said {index}."), Said(newest));
        Assert.Equal(Enumerable.Range(20, 40).Select(index => $"said {index}."), Said(middle));
        Assert.Equal(Enumerable.Range(0, 20).Select(index => $"said {index}."), Said(oldest));
    }

    [Fact]
    public async Task The_newest_page_leads_with_an_older_checklist_without_losing_a_row()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        await fixture.SeedRunAsync();
        var history = new ReplayHistory();
        const string todos = "{\"todos\":[{\"content\":\"Older task\",\"status\":\"pending\"}]}";
        history.AddTool(100, "todo_write", todos, todos);
        for (var index = 0; index < 5; index++) history.AddThinking(index);
        await history.CommitAsync(fixture);

        var (newest, _) = await ReplayAsync(fixture, before: null, limit: 3);
        var rows = newest.GetProperty("activities").EnumerateArray().ToArray();
        // Three newest thinking rows, plus the checklist ahead of them: added, not swapped in.
        Assert.Equal(4, rows.Length);
        Assert.Equal("Older task", rows[0].GetProperty("todos")[0].GetProperty("content").GetString());
        Assert.Equal("reasoning 2", rows[1].GetProperty("detail").GetString());
        Assert.True(newest.GetProperty("has_more").GetBoolean());
        Assert.Equal(3, newest.GetProperty("older_count").GetInt32());

        // The older page holds the same checklist where it happened; clients keep one copy by id.
        var (older, _) = await ReplayAsync(fixture, newest.GetProperty("next_before_sequence").GetInt64(), 3);
        Assert.Contains(rows[0].GetProperty("activity_id").GetString(), Ids(older));
        Assert.False(older.GetProperty("has_more").GetBoolean());
    }

    [Theory]
    [InlineData("0", null, "invalid_cursor")]
    [InlineData("-3", null, "invalid_cursor")]
    [InlineData("abc", null, "invalid_cursor")]
    [InlineData(null, "0", "invalid_limit")]
    [InlineData(null, "501", "invalid_limit")]
    [InlineData(null, "x", "invalid_limit")]
    public async Task A_bad_cursor_or_limit_is_refused_by_name(string? before, string? limit, string code)
    {
        using var factory = new BackendWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var query = string.Join('&', new[]
        {
            before is null ? null : "before_sequence=" + Uri.EscapeDataString(before),
            limit is null ? null : "limit=" + Uri.EscapeDataString(limit),
        }.Where(part => part is not null));

        using var response = await client.GetAsync($"/agent/v1/runs/run_{new string('1', 32)}/activities?{query}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static string[] Ids(JsonElement body) => body.GetProperty("activities").EnumerateArray()
        .Select(row => row.GetProperty("activity_id").GetString()!).ToArray();

    private static async Task CorruptToolResultAsync(BackendHarnessTestFixture fixture, bool newest)
    {
        await using var connection = await fixture.Options.OpenConnectionAsync(default);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_payloads SET authentication_tag=zeroblob(16) WHERE payload_id=(SELECT payload_id FROM agent_events WHERE run_id=$run AND kind='tool.execution_completed' ORDER BY sequence "
            + (newest ? "DESC" : "ASC") + " LIMIT 1)";
        command.Parameters.AddWithValue("$run", ReplayHistory.Run.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task CorruptLatestToolResultAsync(BackendHarnessTestFixture fixture)
    {
        await using var connection = await fixture.Options.OpenConnectionAsync(default);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_payloads SET authentication_tag=zeroblob(16) WHERE payload_id=(SELECT payload_id FROM agent_events WHERE run_id=$run AND kind='tool.execution_completed' ORDER BY sequence DESC LIMIT 1)";
        command.Parameters.AddWithValue("$run", ReplayHistory.Run.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private Task<(JsonElement Body, int Reads)> ReplayAsync(BackendHarnessTestFixture fixture) =>
        ReplayAsync(fixture, before: null, limit: null);

    private async Task<(JsonElement Body, int Reads)> ReplayAsync(BackendHarnessTestFixture fixture, long? before, int? limit)
    {
        var keys = new CountingKeyStore(fixture.KeyStore);
        var payloads = new HarnessPayloadStore(fixture.Options, new HarnessPayloadCryptography(keys), KnownSecretRedactor.Empty);
        var service = new AgentActivitiesService(fixture.EventStore, payloads, new MetadataPolicy());
        var clock = Stopwatch.StartNew();
        var result = await service.ListRunActivitiesAsync(ReplayHistory.Run.Value, before, limit, default);
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

        internal PayloadId AddThinking(int index, string? narration = null)
        {
            var step = new ModelStepId("step_" + (index + 1).ToString("x32"));
            var payload = AddPayload(HarnessPayloadKind.ModelReasoning, $"reasoning {index}", json: false);
            _events.Add(new(NewEvent(), HarnessEventKind.ModelStepStarted, modelStepId: step));
            _events.Add(new(NewEvent(), HarnessEventKind.ModelReasoningRecorded, modelStepId: step, payloadId: payload));
            PayloadId? said = narration is null ? null : AddPayload(HarnessPayloadKind.AssistantText, narration, json: false);
            _events.Add(new(NewEvent(), HarnessEventKind.ModelStepCompleted, modelStepId: step, payloadId: said));
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
