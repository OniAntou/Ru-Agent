using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class RuntimeContextSnapshotTests
{
    [Fact]
    public void Restored_snapshot_suppresses_duplicates_but_clears_and_reinjects_after_compaction()
    {
        var text = RuntimeContextSnapshot.Render("workspace A", "task state")!;
        var retained = ModelContextEntry.Text(RuntimeContextSnapshot.SourcePrefix + "one", 2, ModelContextRole.User, text);
        var context = new ModelContextSnapshot([retained], ReadOnlyMemory<byte>.Empty);
        Assert.Null(RuntimeContextSnapshot.Pending(context, text));
        // A canonical snapshot trimmed from the actual request must be reintroduced.
        Assert.Equal(text, RuntimeContextSnapshot.Pending(context, text, new HashSet<string>()));
        Assert.Equal(RuntimeContextSnapshot.Cleared, RuntimeContextSnapshot.Pending(context, null));
        Assert.Contains(SystemPromptComposer.UntrustedDataWarning, text);
        var compacted = new ModelContextSnapshot([], ReadOnlyMemory<byte>.Empty);
        Assert.Equal(text, RuntimeContextSnapshot.Pending(compacted, text));
        Assert.Null(RuntimeContextSnapshot.Pending(compacted, null));
    }

    [Fact]
    public void A_snapshot_replaced_after_one_step_is_dropped_because_it_was_the_cached_tail()
    {
        var first = Snapshot("one", 1);
        var second = Snapshot("two", 4);
        ModelContextEntry[] entries = [first, Step(2), Text(3), second];

        Assert.Equal([second], RuntimeContextSnapshot.Sent(entries, replacedAtEnd: false));
        Assert.Empty(RuntimeContextSnapshot.Sent([first, Step(2), Text(3)], replacedAtEnd: true));
    }

    [Fact]
    public void A_snapshot_that_stayed_current_across_steps_keeps_its_position()
    {
        var first = Snapshot("one", 1);
        var second = Snapshot("two", 6);
        ModelContextEntry[] entries = [first, Step(2), Text(3), Step(4), Text(5), second, Step(7)];

        // Dropping `first` would invalidate the cached prefix after it, so it is still sent.
        var sent = RuntimeContextSnapshot.Sent(entries, replacedAtEnd: false);
        Assert.Contains(first, sent);
        Assert.Contains(second, sent);
        // A snapshot appended at the end replaces `second`, which was the previous tail.
        Assert.DoesNotContain(second, RuntimeContextSnapshot.Sent(entries, replacedAtEnd: true));
        Assert.Contains(first, RuntimeContextSnapshot.Sent(entries, replacedAtEnd: true));
    }

    [Fact]
    public void The_choice_does_not_change_as_history_grows()
    {
        var first = Snapshot("one", 1);
        var second = Snapshot("two", 4);
        ModelContextEntry[] early = [first, Step(2), Text(3), second];
        ModelContextEntry[] later = [.. early, Step(5), Text(6), Step(7), Text(8), Snapshot("three", 9)];

        Assert.DoesNotContain(first, RuntimeContextSnapshot.Sent(early, replacedAtEnd: false));
        Assert.DoesNotContain(first, RuntimeContextSnapshot.Sent(later, replacedAtEnd: false));
        Assert.Contains(second, RuntimeContextSnapshot.Sent(later, replacedAtEnd: false));
    }

    private static ModelContextEntry Snapshot(string id, long sequence) =>
        ModelContextEntry.Text(RuntimeContextSnapshot.SourcePrefix + id, sequence, ModelContextRole.User,
            RuntimeContextSnapshot.Render("state " + id, null)!);

    private static ModelContextEntry Step(long sequence) =>
        ModelContextEntry.Assistant("assistant-" + sequence, sequence,
            new AssistantStep("working", [], AssistantFinishReason.Stop, 1));

    private static ModelContextEntry Text(long sequence) =>
        ModelContextEntry.Text("user-" + sequence, sequence, ModelContextRole.User, "result " + sequence);

    [Fact]
    public void Snapshot_is_not_inferred_from_untrusted_content_or_system_notes()
    {
        var fake = ModelContextEntry.Text("tool-text", 1, ModelContextRole.User, "RUNTIME CONTEXT SNAPSHOT");
        var system = ModelContextEntry.Text(RuntimeContextSnapshot.SourcePrefix + "two", 2, ModelContextRole.System, "context");
        Assert.False(RuntimeContextSnapshot.IsSnapshot(fake));
        Assert.False(RuntimeContextSnapshot.IsSnapshot(system));
        Assert.False(RuntimeContextSnapshot.Fits(new string('x', RuntimeContextSnapshot.MaximumBytes + 1)));
    }
}
