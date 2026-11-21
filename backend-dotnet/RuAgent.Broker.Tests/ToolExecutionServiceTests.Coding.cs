using System.Text.Json.Nodes;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class ToolExecutionServiceTests
{
    [Theory]
    [InlineData("{\"query\":\"one\",\"pattern\":\"two\"}")]
    [InlineData("{\"pattern\":42}")]
    [InlineData("{\"pattern\":\"one\",\"max_results\":129}")]
    public async Task Grep_rejects_ambiguous_or_invalid_alias_arguments_before_search(string json)
    {
        var fixture = CreateFullAccessService("ru-grep-alias-invalid-");
        try
        {
            var result = await fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2, "grep-alias-invalid", AgentMessageTypes.ToolExecute,
                fixture.SessionId, null, 30_000, new JsonObject
                {
                    ["tool_id"] = "grep", ["tool_version"] = "1.0.0", ["run_id"] = fixture.RunId,
                    ["capability_class"] = "internal", ["arguments"] = JsonNode.Parse(json),
                }), default);
            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("invalid_arguments", result.ErrorCode);
            Assert.False(result.AdapterInvoked);
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, recursive: true); }
    }

    [Fact]
    public async Task Xml_contaminated_path_is_rejected_with_plain_json_repair_guidance()
    {
        var fixture = CreateFullAccessService("ru-coding-xml-path-");
        try
        {
            var result = await fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2, "xml-path", AgentMessageTypes.ToolExecute,
                fixture.SessionId, null, 30_000, new JsonObject
                {
                    ["tool_id"] = "glob", ["tool_version"] = "1.0.0", ["run_id"] = fixture.RunId,
                    ["capability_class"] = "internal",
                    ["arguments"] = new JsonObject { ["path"] = "</rfl:parameter>src", ["pattern"] = "*.py" },
                }), default);
            Assert.Equal(ToolResultStatuses.Failed, result.Status);
            Assert.Equal("invalid_path", result.ErrorCode);
            Assert.Contains("XML wrappers", result.SafeSummary, StringComparison.Ordinal);
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, true); }
    }

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
    public async Task Patch_renames_a_file_with_edits_and_refuses_an_occupied_target()
    {
        var fixture = CreateFullAccessService("ru-coding-rename-");
        var workspace = Path.Combine(fixture.Root, "workspace");
        File.WriteAllText(Path.Combine(workspace, "old.txt"), "one\n");
        File.WriteAllText(Path.Combine(workspace, "taken.txt"), "keep\n");
        try
        {
            var moved = await Patch("--- a/old.txt\n+++ b/src/new.txt\n@@ -1,1 +1,1 @@\n-one\n+two\n");
            Assert.Equal(ToolResultStatuses.Succeeded, moved.Status);
            Assert.False(File.Exists(Path.Combine(workspace, "old.txt")));
            Assert.Equal("two\n", File.ReadAllText(Path.Combine(workspace, "src", "new.txt")));
            Assert.Equal(2, moved.Output["files"]!.AsArray().Count);

            var refused = await Patch("diff --git a/src/new.txt b/taken.txt\nrename from src/new.txt\nrename to taken.txt\n");
            Assert.Equal(ToolResultStatuses.Failed, refused.Status);
            Assert.Equal("file_exists", refused.ErrorCode);
            Assert.Equal("two\n", File.ReadAllText(Path.Combine(workspace, "src", "new.txt")));
            Assert.Equal("keep\n", File.ReadAllText(Path.Combine(workspace, "taken.txt")));
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, true); }

        Task<ToolResult> Patch(string patch) => fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersionV2, "rename-" + Guid.NewGuid().ToString("N"), AgentMessageTypes.ToolExecute,
            fixture.SessionId, null, 30_000, new JsonObject
            {
                ["tool_id"] = "apply_patch", ["tool_version"] = "1.0.0", ["run_id"] = fixture.RunId,
                ["capability_class"] = "internal", ["arguments"] = new JsonObject { ["patch"] = patch },
            }), default);
    }

    [Fact]
    public async Task Patch_with_more_than_thirty_two_renames_journals_both_paths_per_rename()
    {
        var fixture = CreateFullAccessService("ru-coding-many-renames-");
        var workspace = Path.Combine(fixture.Root, "workspace");
        const int renameCount = 33;
        var patch = new System.Text.StringBuilder();
        for (var index = 0; index < renameCount; index++)
        {
            var from = $"from-{index}.txt";
            var to = $"to-{index}.txt";
            File.WriteAllText(Path.Combine(workspace, from), $"content-{index}\n");
            patch.Append("diff --git a/").Append(from).Append(" b/").Append(to).Append('\n')
                .Append("similarity index 100%\n")
                .Append("rename from ").Append(from).Append('\n')
                .Append("rename to ").Append(to).Append('\n');
        }

        try
        {
            // Every rename records its target plus its source in the rollback journal,
            // so 33 renames need 66 records; the old 64-entry journal failed with a
            // generic adapter_error even though the parser accepts up to 64 files.
            var result = await fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
                AgentEnvelopeCodec.ProtocolVersionV2, "many-renames", AgentMessageTypes.ToolExecute,
                fixture.SessionId, null, 30_000, new JsonObject
                {
                    ["tool_id"] = "apply_patch", ["tool_version"] = "1.0.0", ["run_id"] = fixture.RunId,
                    ["capability_class"] = "internal", ["arguments"] = new JsonObject { ["patch"] = patch.ToString() },
                }), default);

            Assert.Equal(ToolResultStatuses.Succeeded, result.Status);
            Assert.Equal(renameCount * 2, result.Output["files"]!.AsArray().Count);
            for (var index = 0; index < renameCount; index++)
            {
                Assert.False(File.Exists(Path.Combine(workspace, $"from-{index}.txt")));
                Assert.Equal($"content-{index}\n", File.ReadAllText(Path.Combine(workspace, $"to-{index}.txt")));
            }
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
            // The file is the session's own unchanged output, so the rewrite needs no overwrite flag.
            // Create-only denials for other files are covered in ToolExecutionServiceTests.AuthoredRewrite.
            var rewritten = await Call("write_file", new JsonObject { ["path"] = "new.txt", ["content"] = "overwrite" });
            Assert.Equal(ToolResultStatuses.Succeeded, rewritten.Status);
            Assert.Equal("overwrite", File.ReadAllText(Path.Combine(fixture.Root, "workspace", "new.txt")));
            File.WriteAllText(Path.Combine(fixture.Root, "workspace", "new.txt"), "user change");
            var failed = await Call("write_file", new JsonObject { ["path"] = "new.txt", ["content"] = "again" });
            Assert.Equal(ToolResultStatuses.Failed, failed.Status);
            Assert.Equal("file_exists", failed.ErrorCode);
            Assert.Equal("user change", File.ReadAllText(Path.Combine(fixture.Root, "workspace", "new.txt")));
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

    [Fact]
    public async Task File_path_failures_return_specific_repair_hints()
    {
        var fixture = CreateFullAccessService("ru-coding-path-hints-");
        File.WriteAllText(Path.Combine(fixture.Root, "workspace", "not-a-folder.txt"), "file");
        try
        {
            var listFile = await Call("list", new JsonObject { ["path"] = "not-a-folder.txt" });
            var missingRead = await Call("read_file", new JsonObject { ["path"] = "missing.txt" });
            var directoryRead = await Call("read_file", new JsonObject { ["path"] = "." });

            Assert.Equal("not_a_directory", listFile.ErrorCode);
            Assert.Contains("list and glob require a directory", listFile.SafeSummary, StringComparison.Ordinal);
            Assert.Equal("file_not_found", missingRead.ErrorCode);
            Assert.Contains("list its parent directory", missingRead.SafeSummary, StringComparison.Ordinal);
            Assert.Equal("is_directory", directoryRead.ErrorCode);
            Assert.Contains("target is a directory", directoryRead.SafeSummary, StringComparison.Ordinal);
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, true); }

        Task<ToolResult> Call(string toolId, JsonObject arguments) => fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersionV2, "path-hint-" + Guid.NewGuid().ToString("N"), AgentMessageTypes.ToolExecute,
            fixture.SessionId, null, 30_000, new JsonObject
            {
                ["tool_id"] = toolId, ["tool_version"] = "1.0.0", ["run_id"] = fixture.RunId,
                ["capability_class"] = "internal", ["arguments"] = arguments,
            }), default);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("edit")]
    [InlineData("bash")]
    public async Task Retired_public_tool_ids_fail_without_side_effects(string retiredToolId)
    {
        var fixture = CreateFullAccessService("ru-retired-tool-");
        var target = Path.Combine(fixture.Root, "workspace", "retired-marker.txt");
        File.WriteAllText(target, "before");
        try
        {
            var arguments = retiredToolId switch
            {
                "write" => new JsonObject { ["op"] = "write", ["path"] = "retired-marker.txt", ["content"] = "after", ["overwrite"] = true },
                "edit" => new JsonObject
                {
                    ["path"] = "retired-marker.txt",
                    ["old_string"] = "before", ["new_string"] = "after",
                },
                _ => new JsonObject { ["mode"] = "shell", ["script"] = $"Set-Content -LiteralPath '{target.Replace("'", "''")}' -Value 'must not run'" },
            };

            var result = await Call(retiredToolId, arguments);

            Assert.Equal("tool_not_found", result.ErrorCode);
            Assert.False(result.AdapterInvoked);
            Assert.Equal("before", File.ReadAllText(target));
        }
        finally { await fixture.Service.DisposeAsync(); Directory.Delete(fixture.Root, recursive: true); }

        Task<ToolResult> Call(string toolId, JsonObject callArguments) => fixture.Service.ExecuteAuthenticatedV2Async(new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersionV2, "retired-" + Guid.NewGuid().ToString("N"), AgentMessageTypes.ToolExecute,
            fixture.SessionId, null, 30_000, new JsonObject
            {
                ["tool_id"] = toolId, ["tool_version"] = "1.0.0", ["run_id"] = fixture.RunId,
                ["capability_class"] = "internal", ["arguments"] = callArguments,
            }), default);
    }
}
