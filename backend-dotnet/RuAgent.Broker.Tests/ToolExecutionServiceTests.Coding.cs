using System.Text.Json.Nodes;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class ToolExecutionServiceTests
{
    [Fact]
    public async Task Explicit_windows_terminal_tool_runs_command_without_a_mode_discriminator()
    {
        var fixture = CreateFullAccessService("ru-coding-terminal-");
        try
        {
            var result = await fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2, "explicit-powershell", AgentMessageTypes.ToolExecute,
                fixture.SessionId, null, 30_000, new JsonObject
                {
                    ["tool_id"] = "powershell", ["tool_version"] = "1.0.0", ["run_id"] = fixture.RunId,
                    ["capability_class"] = "internal",
                    ["arguments"] = new JsonObject { ["script"] = "Write-Output coding-tools-ok", ["purpose"] = "verification" },
                }), default);
            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.Contains("coding-tools-ok", result.Output.ToJsonString());
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Patch_preflights_all_hunks_and_preserves_current_files_on_rejection(bool valid)
    {
        var fixture = CreateFullAccessService("ru-coding-patch-");
        var workspace = Path.Combine(fixture.Root, "workspace");
        File.WriteAllText(Path.Combine(workspace, "a.txt"), "one\r\n");
        File.WriteAllText(Path.Combine(workspace, "b.txt"), "two\r\n");
        try
        {
            var patch = "--- a/a.txt\n+++ b/a.txt\n@@ -1,1 +1,1 @@\n-one\n+changed\n"
                + "--- a/b.txt\n+++ b/b.txt\n@@ -1,1 +1,1 @@\n-" + (valid ? "two" : "missing") + "\n+changed\n";
            var result = await fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2, "multi-patch", AgentMessageTypes.ToolExecute,
                fixture.SessionId, null, 30_000, new JsonObject
                {
                    ["tool_id"] = "apply_patch", ["tool_version"] = "1.0.0", ["run_id"] = fixture.RunId,
                    ["capability_class"] = "internal", ["arguments"] = new JsonObject { ["patch"] = patch },
                }), default);
            Assert.Equal(valid ? ToolResultStatuses.Succeeded : ToolResultStatuses.Failed, result.Status);
            Assert.Equal(valid ? "changed\r\n" : "one\r\n", File.ReadAllText(Path.Combine(workspace, "a.txt")));
            Assert.Equal(valid ? "changed\r\n" : "two\r\n", File.ReadAllText(Path.Combine(workspace, "b.txt")));
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, true); }
    }

    [Fact]
    public async Task Coding_tools_create_then_atomically_edit_without_discriminators()
    {
        var fixture = CreateFullAccessService("ru-coding-tools-");
        try
        {
            var created = await Call("write_file", new JsonObject { ["path"] = "new.txt", ["content"] = "one two" });
            Assert.Equal(ToolResultStatuses.Succeeded, created.Status);
            var edited = await Call("edit_file", new JsonObject { ["path"] = "new.txt", ["edits"] = new JsonArray(
                new JsonObject { ["old_string"] = "one", ["new_string"] = "two" },
                new JsonObject { ["old_string"] = "two", ["new_string"] = "three" }) });
            Assert.Equal(ToolResultStatuses.Succeeded, edited.Status);
            Assert.Equal("two three", File.ReadAllText(Path.Combine(fixture.Root, "workspace", "new.txt")));
            var failed = await Call("write_file", new JsonObject { ["path"] = "new.txt", ["content"] = "overwrite" });
            Assert.Equal(ToolResultStatuses.Failed, failed.Status);
            Assert.Equal("two three", File.ReadAllText(Path.Combine(fixture.Root, "workspace", "new.txt")));
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, recursive: true); }

        Task<ToolResult> Call(string toolId, JsonObject arguments) => fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersionV2, "coding-" + Guid.NewGuid().ToString("N"), AgentMessageTypes.ToolExecute,
            fixture.SessionId, null, 30_000, new JsonObject
            {
                ["tool_id"] = toolId, ["tool_version"] = "1.0.0", ["run_id"] = fixture.RunId,
                ["capability_class"] = "internal", ["arguments"] = arguments,
            }), default);
    }
}
