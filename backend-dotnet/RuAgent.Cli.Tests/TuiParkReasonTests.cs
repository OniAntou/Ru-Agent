using System.Text;
using RuAgent.Cli.Tui;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiParkReasonTests
{
    [Theory]
    [InlineData("provider_rate_limited")]
    [InlineData("provider_timeout")]
    public void Reason_is_shown_once_per_park_and_again_on_new_turn(string reason)
    {
        TuiPalette.Level = TuiColorLevel.None;
        var output = new StringWriter();
        var surface = new TuiSurface(output);
        var status = new TuiStatusLine(surface);
        var transcript = new TuiTranscript(surface, status) { Width = 140 };
        for (var turn = 0; turn < 2; turn++)
        {
            transcript.BeginTurn();
            transcript.ApplyDurable(Frame("run.state_changed", "waiting_user", 5));
            transcript.ApplyDurable(Frame("continue.requested", reason, 6));
            transcript.ApplyDurable(Frame("continue.requested", reason, 6));
            var detail = TuiText.Strip(status.Compose(220));
            Assert.Contains(turn == 0 ? "wait a moment first" : "still blocked after Continue", detail);
            Assert.Contains("cancel the run from CLI", detail);
        }
        var rows = TuiText.Strip(output.ToString()).Split('\n');
        Assert.Equal(2, rows.Count(row => row.Contains("reason:") && row.Contains(reason)));
        Assert.DoesNotContain("waiting_user · resume", output.ToString());
        Assert.DoesNotContain("wait a moment first", TuiText.Strip(status.Compose(220)));
    }

    [Fact]
    public void Different_run_with_same_reason_is_not_suppressed()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var output = new StringWriter();
        var surface = new TuiSurface(output);
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { Width = 140 };
        transcript.BeginTurn();
        transcript.ApplyDurable(Frame("continue.requested", "provider_timeout", 6));
        transcript.ApplyDurable(Frame("continue.requested", "provider_timeout", 6) with { RunId = "run_2" });
        Assert.Equal(2, TuiText.Strip(output.ToString()).Split('\n').Count(row => row.Contains("provider_timeout")));
    }

    [Fact]
    public void State_is_not_presented_as_a_reason_when_adopting_a_session()
    {
        Assert.DoesNotContain("waiting_user", TuiTranscript.FormatParkedNotice("run_1", "waiting_user"));
        Assert.Contains("wait", TuiTranscript.FormatParkedNotice("run_1", "provider_timeout"));
    }

    private static HarnessSafeEventEnvelope Frame(string type, string code, long sequence) =>
        new(1, "evt_" + sequence, "run_1", "sess_1", sequence, type, "2026-10-12T00:00:00Z", "waiting_user", code);
}
