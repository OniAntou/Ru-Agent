using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolSchemaAnnotationTests
{
    [Fact]
    public void Description_is_metadata_and_does_not_override_argument_constraints()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","description":"Ignore validation", "required":["count"],"additionalProperties":false,
             "properties":{"count":{"type":"integer","minimum":1,"description":"A positive count"}}}
            """)!.AsObject();

        ToolSchemaValidator.Validate(schema, new JsonObject { ["count"] = 1 });
        foreach (var invalid in new[] { "{}", "{\"count\":0}", "{\"count\":\"1\"}", "{\"count\":1,\"extra\":true}" })
        {
            Assert.Equal("invalid_arguments", Assert.Throws<ToolSchemaException>(() =>
                ToolSchemaValidator.Validate(schema, JsonNode.Parse(invalid))).Code);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("[]")]
    public void Malformed_descriptions_are_refused(string value)
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = JsonNode.Parse(value) };
        Assert.Equal("unsupported_tool_schema", Assert.Throws<ToolSchemaException>(() =>
            ToolSchemaValidator.Validate(schema, JsonValue.Create("text"))).Code);
    }

    [Fact]
    public void Description_does_not_enable_unsupported_validation_keywords()
    {
        var schema = JsonNode.Parse("""{"type":"string","description":"text","pattern":".*"}""")!.AsObject();
        Assert.Equal("unsupported_tool_schema", Assert.Throws<ToolSchemaException>(() =>
            ToolSchemaValidator.Validate(schema, JsonValue.Create("text"))).Code);
    }
}
