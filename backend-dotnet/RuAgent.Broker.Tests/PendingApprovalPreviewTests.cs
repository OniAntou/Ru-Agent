using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class PendingApprovalPreviewTests
{
    private const int MaxPreviewLength = ApprovalPreviewFields.MaxLength;

    [Fact]
    public void Files_write_preview_is_the_first_512_chars_of_content()
    {
        var content = new string('a', 700);
        var args = new JsonObject
        {
            ["path"] = "notes/demo.txt",
            ["content"] = content,
        };

        var preview = ToolExecutionService.BuildPreview("files.write", args);

        Assert.Equal(content[..MaxPreviewLength], preview);
    }

    [Fact]
    public void Terminal_shell_preview_is_the_script_head()
    {
        var script = "Get-ChildItem -Recurse " + new string('x', 600);
        var args = new JsonObject { ["script"] = script };

        var preview = ToolExecutionService.BuildPreview("terminal.shell", args);

        Assert.Equal(script[..MaxPreviewLength], preview);
    }

    [Fact]
    public void Terminal_run_preview_is_the_bounded_command_head()
    {
        var args = new JsonObject
        {
            ["executable"] = "git",
            ["arguments"] = new JsonArray("commit", "-m", new string('m', 800)),
        };

        var preview = ToolExecutionService.BuildPreview("terminal.run", args);

        Assert.True(preview.Length <= MaxPreviewLength);
        Assert.StartsWith("git commit", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminal_run_preview_without_an_executable_is_empty()
    {
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("terminal.run", new JsonObject { ["arguments"] = new JsonArray("x") }));
        Assert.Equal(
            "git",
            ToolExecutionService.BuildPreview("terminal.run", new JsonObject { ["executable"] = "git" }));
    }

    [Fact]
    public void Files_edit_preview_shows_inline_markers_centered_on_first_match()
    {
        var before = new string('b', 300);
        var after = new string('a', 300);
        var args = new JsonObject
        {
            ["path"] = "notes/demo.txt",
            ["old_string"] = "TARGET",
            ["new_string"] = "REPLACED",
        };

        var preview = ToolExecutionService.BuildPreview("files.edit", args, before + "TARGET" + after);

        Assert.True(preview.Length <= MaxPreviewLength);
        Assert.Contains("[-TARGET-]", preview, StringComparison.Ordinal);
        Assert.Contains("[+REPLACED+]", preview, StringComparison.Ordinal);
        Assert.StartsWith("bbbb", preview, StringComparison.Ordinal);
        Assert.EndsWith("aaaa", preview, StringComparison.Ordinal);
        var markerStart = preview.IndexOf("[-", StringComparison.Ordinal);
        Assert.InRange(markerStart, 200, 290);
    }

    [Fact]
    public void Files_edit_excerpt_is_hard_capped_at_512_chars()
    {
        var args = new JsonObject
        {
            ["path"] = "notes/demo.txt",
            ["old_string"] = "needle",
            ["new_string"] = "replacement",
        };
        var fileText = new string('x', 10_000) + "needle" + new string('y', 10_000);

        var preview = ToolExecutionService.BuildPreview("files.edit", args, fileText);

        Assert.Equal(MaxPreviewLength, preview.Length);
        Assert.Contains("[-needle-]", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Files_edit_falls_back_to_old_and_new_heads_without_a_readable_match()
    {
        var args = new JsonObject
        {
            ["path"] = "notes/missing.txt",
            ["old_string"] = new string('o', 400),
            ["new_string"] = new string('n', 400),
        };

        var missingFile = ToolExecutionService.BuildPreview("files.edit", args, fileText: null);
        var noMatch = ToolExecutionService.BuildPreview("files.edit", args, "unrelated text");

        foreach (var preview in new[] { missingFile, noMatch })
        {
            Assert.True(preview.Length <= MaxPreviewLength);
            Assert.Contains("[-", preview, StringComparison.Ordinal);
            Assert.Contains("[+", preview, StringComparison.Ordinal);
            Assert.StartsWith("[-", preview, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("system.volume")]
    [InlineData("notes.create")]
    [InlineData("unknown.tool")]
    [InlineData("")]
    public void Unknown_tools_get_an_empty_preview(string toolId)
    {
        var args = new JsonObject { ["script"] = "Get-Date", ["content"] = "text" };

        Assert.Equal(string.Empty, ToolExecutionService.BuildPreview(toolId, args));
    }

    [Fact]
    public void Missing_arguments_get_an_empty_preview()
    {
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("files.write", new JsonObject { ["path"] = "a.txt" }));
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("terminal.shell", new JsonObject { ["shell"] = "powershell" }));
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("files.edit", new JsonObject()));
    }

    [Fact]
    public void Malformed_arguments_never_throw_from_preview_building()
    {
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("files.write", new JsonObject { ["content"] = 123 }));
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("terminal.shell", new JsonObject { ["script"] = true }));
    }

    [Fact]
    public void Protocol_roundtrip_preserves_the_optional_preview_field()
    {
        var envelope = new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersion,
            "req-preview",
            AgentMessageTypes.ToolResult,
            "session-1",
            "corr-1",
            30_000,
            new JsonObject
            {
                ["status"] = ToolResultStatuses.ApprovalRequired,
                ["error_code"] = "approval_required",
                ["safe_summary"] = "Approval is required.",
                ["duration_ms"] = 0,
                ["verification_state"] = ToolVerificationStates.Pending,
                ["adapter_invoked"] = false,
                ["output"] = new JsonObject
                {
                    ["approval_id"] = "approval-1",
                    [ApprovalPreviewFields.Field] = "Get-Date",
                },
            });

        var parsed = AgentEnvelopeCodec.Parse(AgentEnvelopeCodec.Serialize(envelope));

        Assert.Equal(
            "Get-Date",
            parsed.Payload["output"]![ApprovalPreviewFields.Field]!.GetValue<string>());
    }

    [Fact]
    public void Protocol_roundtrip_accepts_approval_output_without_a_preview_field()
    {
        var envelope = new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersion,
            "req-no-preview",
            AgentMessageTypes.ToolResult,
            "session-1",
            null,
            30_000,
            new JsonObject
            {
                ["status"] = ToolResultStatuses.ApprovalRequired,
                ["safe_summary"] = "Approval is required.",
                ["duration_ms"] = 0,
                ["verification_state"] = ToolVerificationStates.Pending,
                ["adapter_invoked"] = false,
                ["output"] = new JsonObject { ["approval_id"] = "approval-1" },
            });

        var parsed = AgentEnvelopeCodec.Parse(AgentEnvelopeCodec.Serialize(envelope));

        Assert.False(parsed.Payload["output"]!.AsObject().ContainsKey(ApprovalPreviewFields.Field));
    }
}
