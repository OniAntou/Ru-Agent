using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// write_file stays create-only, except for a file this session created that still holds
/// the session's own last write: rewriting it used to cost a file_exists failure, a read
/// and a retry.
/// </summary>
public sealed partial class ToolExecutionServiceTests
{
    [Fact]
    public async Task Write_file_rewrites_a_file_this_session_created_without_overwrite()
    {
        var fixture = CreateFullAccessService("ru-authored-rewrite-");
        var target = Path.Combine(fixture.Root, "workspace", "cache.py");
        try
        {
            Assert.Equal(ToolResultStatuses.Succeeded, (await Write(fixture, "w1", "cache.py", "v1\n")).Status);
            var rewrite = await Write(fixture, "w2", "cache.py", "v2\n");

            Assert.Equal(ToolResultStatuses.Succeeded, rewrite.Status);
            Assert.Equal("v2\n", File.ReadAllText(target));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public async Task Write_file_rewrite_follows_the_sessions_own_edits()
    {
        var fixture = CreateFullAccessService("ru-authored-edit-");
        var target = Path.Combine(fixture.Root, "workspace", "cache.py");
        try
        {
            Assert.Equal(ToolResultStatuses.Succeeded, (await Write(fixture, "w1", "cache.py", "alpha\n")).Status);
            var edit = await fixture.Service.ExecuteAuthenticatedV2Async(CodingToolEnvelope(fixture, "e1", "edit_file",
                new JsonObject
                {
                    ["path"] = "cache.py",
                    ["edits"] = new JsonArray(new JsonObject { ["old_string"] = "alpha", ["new_string"] = "beta" }),
                }), CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Succeeded, edit.Status);

            var rewrite = await Write(fixture, "w2", "cache.py", "gamma\n");

            Assert.Equal(ToolResultStatuses.Succeeded, rewrite.Status);
            Assert.Equal("gamma\n", File.ReadAllText(target));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public async Task Write_file_stays_create_only_when_someone_else_changed_the_sessions_file()
    {
        var fixture = CreateFullAccessService("ru-authored-changed-");
        var target = Path.Combine(fixture.Root, "workspace", "cache.py");
        try
        {
            Assert.Equal(ToolResultStatuses.Succeeded, (await Write(fixture, "w1", "cache.py", "v1\n")).Status);
            File.WriteAllText(target, "user edit\n");

            var rewrite = await Write(fixture, "w2", "cache.py", "v2\n");

            Assert.Equal(ToolResultStatuses.Failed, rewrite.Status);
            Assert.Equal("file_exists", rewrite.ErrorCode);
            Assert.Equal("user edit\n", File.ReadAllText(target));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public async Task Write_file_stays_create_only_for_a_file_the_session_did_not_create()
    {
        var fixture = CreateFullAccessService("ru-authored-foreign-");
        var target = Path.Combine(fixture.Root, "workspace", "pricing.py");
        try
        {
            File.WriteAllText(target, "original\n");
            // Editing a file the user wrote never makes it the session's own.
            var edit = await fixture.Service.ExecuteAuthenticatedV2Async(CodingToolEnvelope(fixture, "e1", "edit_file",
                new JsonObject
                {
                    ["path"] = "pricing.py",
                    ["edits"] = new JsonArray(new JsonObject { ["old_string"] = "original", ["new_string"] = "edited" }),
                }), CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Succeeded, edit.Status);

            var rewrite = await Write(fixture, "w1", "pricing.py", "replaced\n");

            Assert.Equal(ToolResultStatuses.Failed, rewrite.Status);
            Assert.Equal("file_exists", rewrite.ErrorCode);
            Assert.Equal("edited\n", File.ReadAllText(target));
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public async Task Write_file_stays_create_only_for_a_recreated_file_after_delete_by_someone_else()
    {
        var fixture = CreateFullAccessService("ru-authored-recreated-");
        var target = Path.Combine(fixture.Root, "workspace", "cache.py");
        try
        {
            Assert.Equal(ToolResultStatuses.Succeeded, (await Write(fixture, "w1", "cache.py", "v1\n")).Status);
            var deleted = await fixture.Service.ExecuteAuthenticatedV2Async(CodingToolEnvelope(fixture, "d1", "file_delete",
                new JsonObject { ["path"] = "cache.py" }), CancellationToken.None);
            Assert.Equal(ToolResultStatuses.Succeeded, deleted.Status);
            // The same bytes reappear from outside the session: still not the session's file.
            File.WriteAllText(target, "v1\n");

            var rewrite = await Write(fixture, "w2", "cache.py", "v2\n");

            Assert.Equal(ToolResultStatuses.Failed, rewrite.Status);
            Assert.Equal("file_exists", rewrite.ErrorCode);
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    private static Task<ToolResult> Write(
        (string Root, string SessionId, string RunId, BrokerSession Session, ToolExecutionService Service) fixture,
        string requestId, string path, string content) =>
        fixture.Service.ExecuteAuthenticatedV2Async(CodingToolEnvelope(fixture, requestId, "write_file",
            new JsonObject { ["path"] = path, ["content"] = content }), CancellationToken.None);
}
