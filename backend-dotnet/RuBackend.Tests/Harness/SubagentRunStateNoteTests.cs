using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Subagents;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentRunStateNoteTests
{
    [Fact]
    public void A_settled_detached_child_names_its_result_version()
    {
        var note = SubagentRunStateNote.Describe(
        [
            new("agent_review", SubagentRole.Review, "completed", 2, HarnessRunState.Completed),
            new("agent_working", SubagentRole.Coding, "working"),
            new("agent_failed", SubagentRole.Review, "failed", 1, HarnessRunState.Failed),
        ]);

        Assert.Equal("detached from earlier runs in this session: "
            + "agent_review (Review, completed, result v2 completed), "
            + "agent_working (Coding, working), "
            + "agent_failed (Review, failed, result v1 failed)", note);
    }

    [Fact]
    public void A_child_without_a_result_keeps_only_its_activity()
    {
        var note = SubagentRunStateNote.Describe(
            [new("agent_one", SubagentRole.Review, "completed")]);

        Assert.Equal("detached from earlier runs in this session: agent_one (Review, completed)", note);
    }

    [Fact]
    public void The_note_is_bounded_and_reports_omitted_children()
    {
        var facts = Enumerable.Range(1, SubagentRunStateNote.MaxFacts + 1)
            .Select(index => new SubagentRunStateFact(
                $"agent_{index:D2}", SubagentRole.Review, "working"))
            .ToArray();

        var note = SubagentRunStateNote.Describe(facts, more: true);

        Assert.NotNull(note);
        Assert.Contains("agent_01", note, StringComparison.Ordinal);
        Assert.DoesNotContain("agent_09", note, StringComparison.Ordinal);
        Assert.EndsWith(", …", note, StringComparison.Ordinal);
    }

    [Fact]
    public void No_detached_child_means_no_note()
    {
        Assert.Null(SubagentRunStateNote.Describe([]));
        Assert.Null(SubagentRunStateNote.Describe(null));
    }
}
