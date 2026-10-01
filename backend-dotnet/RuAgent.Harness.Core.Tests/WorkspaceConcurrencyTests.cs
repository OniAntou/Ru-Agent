using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class WorkspaceConcurrencyTests
{
    [Fact]
    public void Hash_mismatch_is_classified_as_unknown_external_change()
    {
        var change = WorkspaceExternalChangeDetector.Detect(
            "src/app.cs",
            expectedSha256: new string('a', 64),
            observedSha256: new string('b', 64));

        Assert.True(change.IsChanged);
        Assert.Equal("unknown_external", change.WriterKind);
        Assert.Equal("src/app.cs", change.NormalizedPath);
    }

    [Fact]
    public void Traversal_aliases_are_rejected_for_external_change_detection()
    {
        Assert.Throws<ArgumentException>(() => WorkspaceExternalChangeDetector.Detect(
            "src/../app.cs",
            new string('a', 64),
            new string('b', 64)));
    }

    [Fact]
    public void Repeated_separators_are_canonicalized_for_external_changes()
    {
        var change = WorkspaceExternalChangeDetector.Detect(
            "src\\app.cs",
            new string('a', 64),
            new string('b', 64));

        Assert.Equal("src/app.cs", change.NormalizedPath);
    }
}
