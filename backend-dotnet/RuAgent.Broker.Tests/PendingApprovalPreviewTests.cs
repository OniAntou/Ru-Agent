using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class PendingApprovalPreviewTests
{
    private const int MaxPreviewLength = ApprovalPreviewFields.MaxLength;

    [Fact]
    public void Multi_edit_excerpts_do_not_split_unicode_surrogate_pairs()
    {
        var text = string.Concat(Enumerable.Repeat("😀", 400));
        var edits = new JsonArray(Enumerable.Range(0, 4).Select(_ => (JsonNode)new JsonObject
            { ["old_string"] = text, ["new_string"] = text }).ToArray());

        var preview = ToolExecutionService.BuildPreview("edit_file", new JsonObject { ["edits"] = edits });

        Assert.Equal(preview, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(preview)));
        Assert.Contains("4 edits", preview, StringComparison.Ordinal);
        Assert.InRange(preview.Length, 1, MaxPreviewLength);
    }

    [Fact]
    public void Multi_edit_preview_names_the_count_and_each_small_replacement()
    {
        var args = new JsonObject { ["path"] = "demo.txt", ["edits"] = new JsonArray(
            new JsonObject { ["old_string"] = "first", ["new_string"] = "FIRST" },
            new JsonObject { ["old_string"] = "second", ["new_string"] = "SECOND" }) };

        var preview = ToolExecutionService.BuildPreview("edit_file", args, "first and second");

        Assert.Contains("2 edits", preview, StringComparison.Ordinal);
        Assert.Contains("[-first-][+FIRST+]", preview, StringComparison.Ordinal);
        Assert.Contains("[-second-][+SECOND+]", preview, StringComparison.Ordinal);
        Assert.InRange(preview.Length, 1, MaxPreviewLength);
    }

    [Fact]
    public void Large_multi_edit_preview_discloses_omitted_edits_within_the_existing_bound()
    {
        var edits = new JsonArray(Enumerable.Range(1, 64).Select(index => (JsonNode)new JsonObject
        {
            ["old_string"] = $"old{index}:" + new string('a', 2000),
            ["new_string"] = $"new{index}:" + new string('b', 2000),
        }).ToArray());
        var args = new JsonObject { ["path"] = "demo.txt", ["edits"] = edits };
        var original = args.ToJsonString();

        var preview = ToolExecutionService.BuildPreview("edit_file", args);

        Assert.Contains("64 edits", preview, StringComparison.Ordinal);
        Assert.Contains("not shown", preview, StringComparison.Ordinal);
        Assert.Contains("all 64 edits", preview, StringComparison.Ordinal);
        Assert.Contains("old1:", preview, StringComparison.Ordinal);
        Assert.Contains("new1:", preview, StringComparison.Ordinal);
        Assert.Contains("old2:", preview, StringComparison.Ordinal);
        Assert.Contains("new2:", preview, StringComparison.Ordinal);
        Assert.InRange(preview.Length, 1, MaxPreviewLength);
        Assert.Equal(original, args.ToJsonString());
    }

    [Fact]
    public void Write_preview_is_the_first_512_chars_of_content()
    {
        var content = new string('a', 700);
        var args = new JsonObject
        {
            ["path"] = "notes/demo.txt",
            ["content"] = content,
        };

        var preview = ToolExecutionService.BuildPreview("write_file", args);

        Assert.Equal(content[..MaxPreviewLength], preview);
    }

    [Fact]
    public void PowerShell_preview_is_the_script_head()
    {
        var script = "Get-ChildItem -Recurse " + new string('x', 600);
        var args = new JsonObject { ["script"] = script };

        var preview = ToolExecutionService.BuildPreview("powershell", args);

        Assert.Equal(script[..MaxPreviewLength], preview);
    }

    [Fact]
    public void Run_process_preview_is_the_bounded_command_head()
    {
        var args = new JsonObject
        {
            ["executable"] = "git",
            ["arguments"] = new JsonArray("commit", "-m", new string('m', 800)),
        };

        var preview = ToolExecutionService.BuildPreview("run_process", args);

        Assert.True(preview.Length <= MaxPreviewLength);
        Assert.StartsWith("git commit", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_process_preview_without_an_executable_is_empty()
    {
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("run_process", new JsonObject { ["arguments"] = new JsonArray("x") }));
        Assert.Equal(
            "git",
            ToolExecutionService.BuildPreview("run_process", new JsonObject { ["executable"] = "git" }));
    }

    [Fact]
    public void Edit_preview_shows_inline_markers_centered_on_first_match()
    {
        var before = new string('b', 300);
        var after = new string('a', 300);
        var args = new JsonObject
        {
            ["path"] = "notes/demo.txt",
            ["edits"] = new JsonArray(new JsonObject { ["old_string"] = "TARGET", ["new_string"] = "REPLACED" }),
        };

        var preview = ToolExecutionService.BuildPreview("edit_file", args, before + "TARGET" + after);

        Assert.True(preview.Length <= MaxPreviewLength);
        Assert.Contains("TARGET", preview, StringComparison.Ordinal);
        Assert.Contains("REPLACED", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_excerpt_is_hard_capped_at_512_chars()
    {
        var args = new JsonObject
        {
            ["path"] = "notes/demo.txt",
            ["edits"] = new JsonArray(new JsonObject { ["old_string"] = "needle", ["new_string"] = "replacement" }),
        };
        var fileText = new string('x', 10_000) + "needle" + new string('y', 10_000);

        var preview = ToolExecutionService.BuildPreview("edit_file", args, fileText);

        Assert.Equal(MaxPreviewLength, preview.Length);
        Assert.Contains("needle", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_falls_back_to_old_and_new_heads_without_a_readable_match()
    {
        var args = new JsonObject
        {
            ["path"] = "notes/missing.txt",
            ["edits"] = new JsonArray(new JsonObject { ["old_string"] = new string('o', 400), ["new_string"] = new string('n', 400) }),
        };

        var missingFile = ToolExecutionService.BuildPreview("edit_file", args, fileText: null);
        var noMatch = ToolExecutionService.BuildPreview("edit_file", args, "unrelated text");

        foreach (var preview in new[] { missingFile, noMatch })
        {
            Assert.True(preview.Length <= MaxPreviewLength);
            Assert.Contains("[-ooo", preview, StringComparison.Ordinal);
            Assert.Contains("[+nnn", preview, StringComparison.Ordinal);
        }
    }

    [Theory]
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
            ToolExecutionService.BuildPreview("write_file", new JsonObject { ["path"] = "a.txt" }));
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("powershell", new JsonObject()));
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("edit_file", new JsonObject()));
    }

    [Fact]
    public void Malformed_arguments_never_throw_from_preview_building()
    {
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("write_file", new JsonObject { ["content"] = 123 }));
        Assert.Equal(
            string.Empty,
            ToolExecutionService.BuildPreview("powershell", new JsonObject { ["script"] = true }));
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
