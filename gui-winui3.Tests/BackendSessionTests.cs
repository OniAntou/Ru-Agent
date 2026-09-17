using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class BackendSessionTests
{
    [Fact]
    public void BackendSession_builds_the_http_endpoint_from_a_loopback_port()
    {
        var session = BackendSession.FromLoopback(18001, "desktop-secret");

        Assert.Equal(new Uri("http://127.0.0.1:18001/"), session.HttpBaseUri);
        Assert.Equal("desktop-secret", session.AuthSecret);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void BackendSession_rejects_ports_outside_dynamic_loopback_range(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BackendSession.FromLoopback(port, "secret"));
    }

    [Fact]
    public void SessionInfo_deserializes_parent_and_fork_metadata()
    {
        var json = """
        {
            "id": "sess-child",
            "title": "Child Session",
            "created_at": "2026-09-08T00:00:00Z",
            "updated_at": "2026-09-08T00:00:00Z",
            "message_count": 5,
            "parent_session_id": "sess-root",
            "forked_from_message_id": "msg-123"
        }
        """;

        var session = System.Text.Json.JsonSerializer.Deserialize<RuApp.Models.SessionInfo>(json);
        Assert.NotNull(session);
        Assert.Equal("sess-child", session.Id);
        Assert.Equal("sess-root", session.ParentSessionId);
        Assert.Equal("msg-123", session.ForkedFromMessageId);
    }

    [Fact]
    public void SessionInfo_deserializes_nullable_workspace_fields()
    {
        const string json = """
            {"id":"s1","title":"T","created_at":"2026-09-17T00:00:00Z","updated_at":"2026-09-17T01:00:00Z",
             "message_count":2,"workspace_id":"ws_1","workspace_label":"LLM","workspace_path":"D:\\LLM"}
            """;

        var info = System.Text.Json.JsonSerializer.Deserialize<RuApp.Models.SessionInfo>(json)!;

        Assert.Equal("ws_1", info.WorkspaceId);
        Assert.Equal("LLM", info.WorkspaceLabel);
        Assert.Equal(@"D:\LLM", info.WorkspacePath);
    }

    [Fact]
    public void SessionInfo_missing_workspace_fields_deserialize_as_null()
    {
        const string json = """
            {"id":"s1","title":"T","created_at":"2026-09-17T00:00:00Z","updated_at":"2026-09-17T01:00:00Z","message_count":2}
            """;

        var info = System.Text.Json.JsonSerializer.Deserialize<RuApp.Models.SessionInfo>(json)!;

        Assert.Null(info.WorkspaceId);
        Assert.Null(info.WorkspaceLabel);
        Assert.Null(info.WorkspacePath);
    }
}
