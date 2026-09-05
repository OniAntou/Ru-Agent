using System.Text.Json;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class ModelToolSelectionParserTests
{
    private static readonly IReadOnlySet<string> Advertised = new HashSet<string>(["files.read", "files.write"], StringComparer.Ordinal);

    [Fact]
    public void Parse_accepts_exactly_one_bounded_tool_selection_object()
    {
        var selection = ModelToolSelectionParser.Parse(
            "{\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"README.md\"}}",
            Advertised);

        Assert.Equal("files.read", selection.ToolId);
        Assert.Equal("1.0.0", selection.ToolVersion);
        Assert.Equal("README.md", selection.Arguments.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("not json", "invalid_json")]
    [InlineData("{\"tool_id\":\"unknown\",\"tool_version\":\"1.0.0\",\"arguments\":{}}", "unknown_tool")]
    [InlineData("{\"tool_id\":\"files.read\",\"tool_version\":\"1\",\"arguments\":{}}", "invalid_tool_version")]
    [InlineData("prefix {\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{}}", "invalid_json")]
    public void Parse_rejects_untrusted_or_invalid_selection(string text, string code)
    {
        var error = Assert.Throws<ModelToolSelectionException>(() => ModelToolSelectionParser.Parse(text, Advertised));

        Assert.Equal(code, error.Code);
        Assert.True(error.Message.Length <= 64);
    }

    [Fact]
    public void Parse_rejects_duplicate_keys_and_oversized_arguments()
    {
        var duplicate = "{\"tool_id\":\"files.read\",\"tool_id\":\"files.write\",\"tool_version\":\"1.0.0\",\"arguments\":{}}";
        Assert.Equal("duplicate_key", Assert.Throws<ModelToolSelectionException>(() => ModelToolSelectionParser.Parse(duplicate, Advertised)).Code);

        var oversized = "{\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"value\":\"" + new string('x', 70_000) + "\"}}";
        Assert.Equal("arguments_too_large", Assert.Throws<ModelToolSelectionException>(() => ModelToolSelectionParser.Parse(oversized, Advertised)).Code);
    }

    [Fact]
    public void Parse_accepts_markdown_code_fence_enclosed_json()
    {
        var raw = "```json\n{\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"README.md\"}}\n```";
        var selection = ModelToolSelectionParser.Parse(raw, Advertised);

        Assert.Equal("files.read", selection.ToolId);
        Assert.Equal("1.0.0", selection.ToolVersion);
        Assert.Equal("README.md", selection.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void Parse_accepts_reasoning_think_tags_before_json()
    {
        var raw = "<think>\nLet's read README.md to understand the setup.\n</think>\n```json\n{\"tool_id\":\"files.read\",\"tool_version\":\"1.0.0\",\"arguments\":{\"path\":\"README.md\"}}\n```";
        var selection = ModelToolSelectionParser.Parse(raw, Advertised);

        Assert.Equal("files.read", selection.ToolId);
        Assert.Equal("1.0.0", selection.ToolVersion);
        Assert.Equal("README.md", selection.Arguments.GetProperty("path").GetString());
    }
}
