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
}
