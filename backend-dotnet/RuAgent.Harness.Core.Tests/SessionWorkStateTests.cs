using System.Text;
using RuAgent.Harness.Core.Context;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class SessionWorkStateTests
{
    private static readonly SessionWorkState Sample = new(
        new SessionId("11111111111111111111111111111111"),
        new WorkspaceId("ws_22222222222222222222222222222222"),
        new string('a', 64),
        new RunId("run_33333333333333333333333333333333"),
        1,
        "evt_44444444444444444444444444444444",
        new SessionWorkFact("Preserve behavior while fixing the parser", "run_33333333333333333333333333333333", "evt_44444444444444444444444444444444"),
        [new SessionWorkFact("Keep the public route set unchanged", "run_33333333333333333333333333333333", "evt_44444444444444444444444444444444")],
        [new SessionWorkFact("Earlier user request", "run_33333333333333333333333333333333", "evt_44444444444444444444444444444444")],
        false, false,
        [new SessionWorkItem("Inspect the call site", SessionWorkItemStatus.Open, SessionWorkItemSource.Model,
            "run_33333333333333333333333333333333", "evt_44444444444444444444444444444444")],
        [new SessionObservedFile("src/parser.cs", new string('b', 64), "run_33333333333333333333333333333333",
            "evt_44444444444444444444444444444444", DateTimeOffset.Parse("2026-10-04T12:00:00Z"))],
        [new SessionVerificationRecord("dotnet test ParserTests", "test_assertion_failed", false,
            "run_33333333333333333333333333333333", "evt_44444444444444444444444444444444",
            DateTimeOffset.Parse("2026-10-04T12:01:00Z"))],
        new SessionWorkFact("ParserTests still fail", "run_33333333333333333333333333333333", "evt_44444444444444444444444444444444"),
        new SessionWorkFact("Inspect the failing assertion", "run_33333333333333333333333333333333", "evt_44444444444444444444444444444444"));

    [Fact]
    public void Codec_roundtrips_provenance_and_marks_model_tasks_untrusted()
    {
        var restored = SessionWorkStateCodec.Deserialize(SessionWorkStateCodec.Serialize(Sample));

        Assert.Equal(Sample.UserGoal, restored.UserGoal);
        Assert.Equal(Sample.UserConstraints, restored.UserConstraints);
        Assert.Equal(Sample.EarlierUserIntent, restored.EarlierUserIntent);
        Assert.Equal(SessionWorkItemSource.Model, Assert.Single(restored.Tasks).Source);
        Assert.Equal(SessionWorkItemStatus.Open, restored.Tasks[0].Status);
        Assert.Equal(Sample.SourceRunId, restored.SourceRunId);
    }

    [Fact]
    public void Constructor_rejects_more_than_sixteen_list_items()
    {
        var error = Assert.Throws<ArgumentException>(() => new SessionWorkState(
            Sample.SessionId, Sample.WorkspaceId, Sample.WorkspaceIdentitySha256,
            Sample.SourceRunId, 1, Sample.SourceEventId,
            new SessionWorkFact("goal", Sample.SourceRunId.Value, Sample.SourceEventId),
            [],
            Enumerable.Repeat(new SessionWorkFact("constraint", Sample.SourceRunId.Value, Sample.SourceEventId), 17).ToArray(),
            false, false,
            [], [], [], null, null));

        Assert.Contains("list exceeds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Codec_rejects_oversized_and_invalid_utf8_envelopes()
    {
        Assert.Throws<InvalidDataException>(() => SessionWorkStateCodec.Deserialize(
            new byte[SessionWorkState.MaximumUtf8Bytes + 1]));
        Assert.Throws<DecoderFallbackException>(() => SessionWorkStateCodec.Deserialize([0xff]));
    }

    [Fact]
    public void Codec_rejects_unknown_duplicate_missing_and_future_version_fields()
    {
        var json = Encoding.UTF8.GetString(SessionWorkStateCodec.Serialize(Sample));
        Assert.Throws<InvalidDataException>(() => SessionWorkStateCodec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace("\"version\":1", "\"version\":1,\"future\":true", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => SessionWorkStateCodec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace("\"version\":1", "\"version\":1,\"version\":2", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => SessionWorkStateCodec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace("\"nextAction\":", "\"removed\":null,\"nextAction\":", StringComparison.Ordinal)
                .Replace("\"nextAction\":null,", string.Empty, StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => SessionWorkStateCodec.Deserialize(
            Encoding.UTF8.GetBytes(json.Replace("\"formatVersion\":1", "\"formatVersion\":2", StringComparison.Ordinal))));
    }

    [Fact]
    public void Run_reducer_preserves_original_user_provenance_through_ten_turns()
    {
        var state = Sample;
        for (var turn = 1; turn <= 9; turn++)
        {
            var runId = new RunId("run_" + turn.ToString("x32"));
            var eventId = new EventId("evt_" + turn.ToString("x32"));
            state = SessionWorkStateReducer.StartRun(state, state.SessionId, state.WorkspaceId,
                state.WorkspaceIdentitySha256, runId, eventId, $"Follow-up turn {turn}");
        }

        Assert.Contains(state.EarlierUserIntent, fact => fact.Text == Sample.UserGoal.Text
            && fact.SourceRunId == Sample.UserGoal.SourceRunId
            && fact.SourceEventId == Sample.UserGoal.SourceEventId);
        Assert.Equal(10, state.Version);
        Assert.False(state.EarlierUserIntentOmitted);
    }

    [Fact]
    public void Run_reducer_carries_explicit_english_and_vietnamese_constraints_with_provenance()
    {
        var firstRun = new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var firstEvent = new EventId("evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var state = SessionWorkStateReducer.StartRun(null, Sample.SessionId, Sample.WorkspaceId,
            Sample.WorkspaceIdentitySha256, firstRun, firstEvent,
            "Keep the public API unchanged.\nĐừng thay đổi schema JSON.\nThe parser is in Parser.cs.");

        var nextRun = new RunId("run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var nextEvent = new EventId("evt_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        state = SessionWorkStateReducer.StartRun(state, Sample.SessionId, Sample.WorkspaceId,
            Sample.WorkspaceIdentitySha256, nextRun, nextEvent, "Continue the parser fix.");

        Assert.Contains(state.UserConstraints, fact => fact.Text == "Keep the public API unchanged."
            && fact.SourceRunId == firstRun.Value && fact.SourceEventId == firstEvent.Value);
        Assert.Contains(state.UserConstraints, fact => fact.Text == "Đừng thay đổi schema JSON."
            && fact.SourceRunId == firstRun.Value && fact.SourceEventId == firstEvent.Value);
        Assert.DoesNotContain(state.UserConstraints, fact => fact.Text == "The parser is in Parser.cs.");
    }

    [Fact]
    public void Run_reducer_bounds_constraint_capture_while_preserving_an_anchor_and_recent_rules()
    {
        SessionWorkState? state = null;
        for (var turn = 1; turn <= 20; turn++)
        {
            var runId = new RunId("run_" + turn.ToString("x32"));
            var eventId = new EventId("evt_" + turn.ToString("x32"));
            state = SessionWorkStateReducer.StartRun(state, Sample.SessionId, Sample.WorkspaceId,
                Sample.WorkspaceIdentitySha256, runId, eventId, $"Keep change-{turn} stable.");
        }

        Assert.InRange(state!.UserConstraints.Count, 1, SessionWorkState.MaximumItems);
        Assert.Contains(state.UserConstraints, fact => fact.Text == "Keep change-1 stable."
            && fact.SourceRunId == "run_00000000000000000000000000000001");
        Assert.Contains(state.UserConstraints, fact => fact.Text == "Keep change-20 stable."
            && fact.SourceEventId == "evt_00000000000000000000000000000014");
    }

    [Fact]
    public void Constraint_overflow_announces_partial_intent_even_without_earlier_turns()
    {
        var prompt = string.Join('\n', Enumerable.Range(0, 17).Select(i => $"Never change contract-{i}."));
        var state = SessionWorkStateReducer.StartRun(null, Sample.SessionId, Sample.WorkspaceId,
            Sample.WorkspaceIdentitySha256, Sample.SourceRunId, new EventId(Sample.SourceEventId), prompt);
        Assert.Empty(state.EarlierUserIntent);
        Assert.Equal(16, state.UserConstraints.Count);
        Assert.True(state.EarlierUserIntentOmitted);
        Assert.Contains("constraints", SessionWorkStateRenderer.Render(state), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("omitted", SessionWorkStateRenderer.Render(state), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CRITICAL: never edit generated files")]
    [InlineData("Always preserve public signatures")]
    [InlineData("Nhớ giữ nguyên API hiện tại")]
    [InlineData("Lưu ý: không được sửa file generated")]
    public void Labelled_imperative_constraints_survive_beyond_the_raw_intent_tail(string constraint)
    {
        var state = SessionWorkStateReducer.StartRun(null, Sample.SessionId, Sample.WorkspaceId,
            Sample.WorkspaceIdentitySha256, Sample.SourceRunId, new EventId(Sample.SourceEventId), "initial task");
        for (var turn = 1; turn <= 40; turn++)
            state = SessionWorkStateReducer.StartRun(state, Sample.SessionId, Sample.WorkspaceId,
                Sample.WorkspaceIdentitySha256, new RunId("run_" + turn.ToString("x32")),
                new EventId("evt_" + turn.ToString("x32")), turn == 1 ? constraint : "continue " + turn);
        Assert.Contains(state.UserConstraints, fact => fact.Text == constraint);
        Assert.DoesNotContain(state.EarlierUserIntent, fact => fact.Text == constraint);
    }

    [Fact]
    public void Run_reducer_falls_back_within_the_work_state_byte_cap_for_many_long_constraints()
    {
        var prompt = string.Join('\n', Enumerable.Range(1, 8)
            .Select(number => $"Keep requirement-{number} stable: {new string('x', 950)}."));
        var state = SessionWorkStateReducer.StartRun(null, Sample.SessionId, Sample.WorkspaceId,
            Sample.WorkspaceIdentitySha256, Sample.SourceRunId, new EventId("evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), prompt);

        Assert.Contains(state.UserConstraints, fact => fact.Text.StartsWith("Keep requirement-8 stable:", StringComparison.Ordinal));
        Assert.True(SessionWorkStateCodec.Serialize(state).Length <= SessionWorkState.MaximumUtf8Bytes);
    }

    [Fact]
    public void Work_state_renderer_hides_intent_from_rewound_runs_but_keeps_receipt_observations()
    {
        var hiddenRun = new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var hiddenEvent = new EventId("evt_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var state = SessionWorkStateReducer.StartRun(Sample, Sample.SessionId, Sample.WorkspaceId,
            Sample.WorkspaceIdentitySha256, hiddenRun, hiddenEvent, "Keep hidden contract unchanged.");
        var visibleRun = new RunId("run_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var visibleEvent = new EventId("evt_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        state = SessionWorkStateReducer.StartRun(state, Sample.SessionId, Sample.WorkspaceId,
            Sample.WorkspaceIdentitySha256, visibleRun, visibleEvent, "Keep visible contract unchanged.");

        var rendered = SessionWorkStateRenderer.Render(state,
            visibleIntentRunIds: new HashSet<string>(StringComparer.Ordinal) { visibleRun.Value });

        Assert.Contains("Keep visible contract unchanged.", rendered);
        Assert.DoesNotContain("Keep hidden contract unchanged.", rendered);
        Assert.DoesNotContain(Sample.UserGoal.Text, rendered);
        Assert.Contains("hidden by the current visible conversation", rendered);
        Assert.Contains("Receipt-observed file: src/parser.cs", rendered);
    }

    [Fact]
    public void Run_reducer_bounds_multibyte_intent_history_and_marks_omissions()
    {
        var state = Sample;
        for (var turn = 1; turn <= 10; turn++)
        {
            var runId = new RunId("run_" + (turn + 20).ToString("x32"));
            var eventId = new EventId("evt_" + (turn + 20).ToString("x32"));
            state = SessionWorkStateReducer.StartRun(state, state.SessionId, state.WorkspaceId,
                state.WorkspaceIdentitySha256, runId, eventId, new string('漢', 1200));
        }

        Assert.True(state.EarlierUserIntentOmitted);
        Assert.Equal(11, state.Version);
        Assert.Equal(new string('漢', 1200), state.UserGoal.Text);
        Assert.Contains(state.EarlierUserIntent, fact => fact.SourceRunId == Sample.UserGoal.SourceRunId
            && fact.SourceEventId == Sample.UserGoal.SourceEventId);
        Assert.True(SessionWorkStateCodec.Serialize(state).Length <= SessionWorkState.MaximumUtf8Bytes);
    }
}
