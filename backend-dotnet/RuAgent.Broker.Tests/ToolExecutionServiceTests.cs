using System.Text.Json.Nodes;
using RuAgent.Broker.Tools;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ToolExecutionServiceTests
{
    [Fact]
    public void Schema_validator_rejects_missing_required_property()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("operation"),
            ["properties"] = new JsonObject
            {
                ["operation"] = new JsonObject { ["type"] = "string" },
            },
        };

        var error = Assert.Throws<ToolSchemaException>(() =>
            ToolSchemaValidator.Validate(schema, new JsonObject()));

        Assert.Equal("invalid_arguments", error.Code);
    }

    [Fact]
    public void Schema_validator_rejects_unsupported_keywords()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["oneOf"] = new JsonArray(),
        };

        var error = Assert.Throws<ToolSchemaException>(() =>
            ToolSchemaValidator.Validate(schema, new JsonObject()));

        Assert.Equal("unsupported_tool_schema", error.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Schema_validator_enforces_integer_bounds(int level)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["level"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 0,
                    ["maximum"] = 100,
                },
            },
        };

        var error = Assert.Throws<ToolSchemaException>(() =>
            ToolSchemaValidator.Validate(schema, new JsonObject { ["level"] = level }));

        Assert.Equal("invalid_arguments", error.Code);
    }

    [Fact]
    public void Schema_validator_accepts_an_in_range_integer()
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["level"] = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 0,
                    ["maximum"] = 100,
                },
            },
        };

        ToolSchemaValidator.Validate(schema, new JsonObject { ["level"] = 2 });
    }
}
