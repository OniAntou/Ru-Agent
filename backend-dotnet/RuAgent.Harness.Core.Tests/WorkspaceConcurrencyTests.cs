using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class WorkspaceConcurrencyTests
{
    [Fact]
    public void Overlapping_binary_claim_is_rejected_but_text_claim_is_mergeable()
    {
        var existing = new WorkspacePathClaim("src/app.cs", WorkspacePathKind.Text);

        Assert.Equal(
            WorkspaceClaimDecision.MergeableTextOverlap,
            WorkspaceConcurrencyDecision.EvaluateClaim(existing, new WorkspacePathClaim("src/app.cs", WorkspacePathKind.Text)));
        Assert.Equal(
            WorkspaceClaimDecision.BinaryConflict,
            WorkspaceConcurrencyDecision.EvaluateClaim(existing, new WorkspacePathClaim("src/app.cs", WorkspacePathKind.Binary)));
    }

    [Fact]
    public void Sequence_increments_monotonically_and_overflow_fails_closed()
    {
        Assert.Equal(8, WorkspaceConcurrencyDecision.NextCommitSequence(7));
        Assert.Throws<OverflowException>(() => WorkspaceConcurrencyDecision.NextCommitSequence(long.MaxValue));
    }

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
}
