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
    public void Snapshot_is_not_inferred_from_untrusted_content_or_system_notes()
    {
        var fake = ModelContextEntry.Text("tool-text", 1, ModelContextRole.User, "RUNTIME CONTEXT SNAPSHOT");
        var system = ModelContextEntry.Text(RuntimeContextSnapshot.SourcePrefix + "two", 2, ModelContextRole.System, "context");
        Assert.False(RuntimeContextSnapshot.IsSnapshot(fake));
        Assert.False(RuntimeContextSnapshot.IsSnapshot(system));
        Assert.False(RuntimeContextSnapshot.Fits(new string('x', RuntimeContextSnapshot.MaximumBytes + 1)));
    }
}
