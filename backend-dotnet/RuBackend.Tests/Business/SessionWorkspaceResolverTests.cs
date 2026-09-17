using System;
using System.Text.Json;
using RuBackend.Business.Sessions;
using Xunit;

namespace RuBackend.Tests.Business;

public sealed class SessionWorkspaceResolverTests
{
    [Fact]
    public void SessionSummary_roundtrips_nullable_workspace_fields()
    {
        var summary = new SessionSummary(
            "sess_1",
            "Title",
            DateTimeOffset.Parse("2026-09-17T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-17T01:00:00Z"),
            3,
            null,
            null,
            "ws_abc",
            "LLM",
            @"D:\LLM");

        var json = JsonSerializer.Serialize(summary);
        var back = JsonSerializer.Deserialize<SessionSummary>(json)!;

        Assert.Equal("ws_abc", back.WorkspaceId);
        Assert.Equal("LLM", back.WorkspaceLabel);
        Assert.Equal(@"D:\LLM", back.WorkspacePath);
    }

    [Fact]
    public void WorkspaceLabel_uses_folder_name_of_root()
    {
        Assert.Equal("LLM", SessionWorkspaceResolver.LabelForRoot(@"D:\LLM"));
        Assert.Equal("LLM", SessionWorkspaceResolver.LabelForRoot(@"D:\LLM\"));
        Assert.Equal("workspace", SessionWorkspaceResolver.LabelForRoot(@"D:\"));
    }

    [Fact]
    public void LatestWorkspaceSql_returns_newest_run_per_session()
    {
        var sql = SessionWorkspaceResolver.LatestWorkspaceSql;
        Assert.Contains("PARTITION BY session_id", sql, StringComparison.Ordinal);
        Assert.Contains("created_at_utc", sql, StringComparison.OrdinalIgnoreCase);
    }
}
