using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class AssistantTextReconcilerTests
{
    private const string Full =
        "Hi! How can I help you today? I can read and edit files, run commands, search your workspace or the web, and more";

    [Fact]
    public void Nothing_durable_means_nothing_to_do()
    {
        Assert.Equal(AssistantTextRecovery.None, AssistantTextReconciler.Decide("partial", string.Empty));
        Assert.Equal(AssistantTextRecovery.None, AssistantTextReconciler.Decide("partial", null));
    }

    [Fact]
    public void A_fully_streamed_reply_needs_no_recovery()
    {
        Assert.Equal(AssistantTextRecovery.Complete, AssistantTextReconciler.Decide(Full, Full));
        Assert.Equal(
            AssistantTextRecovery.Complete,
            AssistantTextReconciler.Decide(Full, "the run recorded less than the terminal showed"));
    }

    [Fact]
    public void A_missing_tail_is_restored_by_appending_the_suffix()
    {
        const string streamed = "Hi! How can I help you today? I can read and edit files";
        var decision = AssistantTextReconciler.Decide(streamed, Full);

        Assert.Equal(AssistantTextRecovery.AppendSuffix, decision);
        Assert.Equal(", run commands, search your workspace or the web, and more", AssistantTextReconciler.SuffixFor(decision, streamed, Full));
    }

    [Fact]
    public void An_empty_stream_prints_the_whole_answer()
    {
        // Nothing arrived, so there is no earlier text to interleave with and the complete
        // reply is the correct thing to print.
        var decision = AssistantTextReconciler.Decide(string.Empty, Full);

        Assert.Equal(AssistantTextRecovery.AppendSuffix, decision);
        Assert.Equal(Full, AssistantTextReconciler.SuffixFor(decision, string.Empty, Full));
    }

    [Fact]
    public void A_missing_head_is_reported_rather_than_repaired()
    {
        // The case the live hub actually causes. The head is durable but unreachable in a
        // terminal that has already printed the tail, and appending it would interleave the
        // two into a mangled sentence.
        const string streamed = "or the web, and more";

        Assert.Equal(
            AssistantTextRecovery.ReportTruncation,
            AssistantTextReconciler.Decide(streamed, Full));
        Assert.Equal(string.Empty, AssistantTextReconciler.SuffixFor(AssistantTextRecovery.ReportTruncation, streamed, Full));
    }

    // ------------------------------------------------------------------
    // Prose payload filtering: a model step that only calls tools stores its
    // record, not prose, and printing it as the recovered reply raw JSON lines.
    // ------------------------------------------------------------------

    private const string StepJson =
        "{\"text\":null,\"tool_calls\":[{\"provider_call_id\":\"call-cc959419\",\"tool_id\":\"list\",\"tool_version\":\"1.0.0\","
        + "\"arguments\":{\"path\":\"\",\"max_entries\":64}}],\"finish_reason\":\"tool_calls\",\"usage\":{\"input_tokens\":7395,\"model\":\"nvidia/nemotron-3.5-super-vl:free\"}}";

    private const string StepJsonWithText =
        "{\"text\":\"done\",\"tool_calls\":[],\"finish_reason\":\"stop\",\"model\":\"x:free\"}";

    [Fact]
    public void Tool_call_step_records_are_not_recovered_as_prose()
    {
        var kept = AssistantTextReconciler.ProsePayloads([Full, StepJson, StepJsonWithText, null, ""]);

        Assert.Equal([Full], kept);
    }

    [Fact]
    public void Plain_prose_and_json_reply_survive_the_filter()
    {
        const string jsonReply = "{\"source\":\"giavang.net\",\"prices\": [140600, 143600]}";
        const string textReply = "So du khong doi so voi hom qua (18/10), khong bien dong.";
        const string arrayReply = "[\"SJC\", \"DOJI\"]";

        var kept = AssistantTextReconciler.ProsePayloads(
            ["Bảng giá:", jsonReply, textReply, arrayReply, "{\"note\": \"finish_reason alone is not a step\"}"]);

        Assert.Equal(["Bảng giá:", jsonReply, textReply, arrayReply, "{\"note\": \"finish_reason alone is not a step\"}"], kept);
    }

    [Fact]
    public void Broken_brace_prose_is_kept_verbatim()
    {
        // A prose reply may open a JSON-ish block the parser rejects; never drop text
        // just because it starts with a brace.
        const string broken = "{\"answer: 0.007347 USD";

        Assert.Equal([broken], AssistantTextReconciler.ProsePayloads([broken]));
    }
}
