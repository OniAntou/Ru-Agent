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
}
