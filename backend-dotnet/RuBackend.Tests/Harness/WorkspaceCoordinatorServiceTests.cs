using RuAgent.Harness.Core;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class WorkspaceCoordinatorServiceTests
{
    [Fact]
    public void Shared_claim_evaluation_allows_disjoint_and_text_overlap_but_rejects_binary_overlap()
    {
        var existing = new[] { new WorkspacePathClaim("src/app.cs", WorkspacePathKind.Text) };

        var disjoint = WorkspaceCoordinatorService.EvaluateClaims(
            existing,
            [new WorkspacePathClaim("src/test.cs", WorkspacePathKind.Text)]);
        var textOverlap = WorkspaceCoordinatorService.EvaluateClaims(
            existing,
            [new WorkspacePathClaim("src/app.cs", WorkspacePathKind.Text)]);
        var binaryOverlap = WorkspaceCoordinatorService.EvaluateClaims(
            existing,
            [new WorkspacePathClaim("src/app.cs", WorkspacePathKind.Binary)]);

        Assert.True(disjoint.Accepted);
        Assert.True(textOverlap.Accepted);
        Assert.False(binaryOverlap.Accepted);
        Assert.Equal("concurrent_binary_conflict", binaryOverlap.ErrorCode);
    }
}
