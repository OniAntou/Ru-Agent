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
