using System.Text.Json;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests.Harness;

public sealed class WorkspaceConcurrencyContractTests
{
    [Fact]
    public void Shared_policy_round_trips_with_snake_case_fields()
    {
        var policy = new WorkspaceConcurrencyPolicy(
            WorkspaceConcurrencyMode.Shared,
            MaxConcurrentRuns: 3,
            LeaseTtlSeconds: 45,
            MergeTextConflicts: true,
            ExternalWriterDetection: true);

        var json = JsonSerializer.Serialize(policy);
        Assert.Contains("\"mode\":\"shared\"", json);
        Assert.Contains("\"max_concurrent_runs\":3", json);
        Assert.Contains("\"lease_ttl_seconds\":45", json);

        var restored = JsonSerializer.Deserialize<WorkspaceConcurrencyPolicy>(json);
        Assert.NotNull(restored);
        Assert.Equal(WorkspaceConcurrencyMode.Shared, restored!.Mode);
        Assert.Equal(3, restored.MaxConcurrentRuns);
        Assert.True(restored.MergeTextConflicts);
    }

    [Fact]
    public void Read_only_write_set_is_rejected_when_it_contains_paths()
    {
        var error = Assert.Throws<AgentProtocolException>(() =>
            WorkspaceWriteSet.Validate(readOnly: true, ["src/app.cs"]));

        Assert.Equal("invalid_terminal_write_set", error.Code);
    }

    [Fact]
    public void Write_set_rejects_parent_traversal()
    {
        var error = Assert.Throws<AgentProtocolException>(() =>
            WorkspaceWriteSet.Validate(readOnly: false, ["../secrets.txt"]));

        Assert.Equal("invalid_terminal_write_set", error.Code);
    }

    [Fact]
    public void Activity_item_preserves_unknown_external_writer_kind()
    {
        var item = new WorkspaceActivityItem(
            EventId: "evt_01",
            WorkspaceId: "ws_0123456789abcdef0123456789abcdef",
            Kind: "workspace_external_change_detected",
            Path: "src/app.cs",
            WriterKind: WorkspaceWriterKind.UnknownExternal,
            Sequence: 7,
            CreatedAtUtc: "2026-09-02T10:00:00Z",
            Details: null);

        var json = JsonSerializer.Serialize(item);
        Assert.Contains("\"writer_kind\":\"unknown_external\"", json);
    }
}
