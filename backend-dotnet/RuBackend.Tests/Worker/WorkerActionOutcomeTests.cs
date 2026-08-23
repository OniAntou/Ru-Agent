using System.Text.Json.Nodes;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class WorkerActionOutcomeTests
{
    private static readonly WorkerAdvertisedTools VolumeOnly =
        new(new HashSet<(string ToolId, string ToolVersion)>
        {
            ("system.volume", "1.0.0"),
        });

    private static WorkerAdvertisedTools Advertised(string toolId, string toolVersion) =>
        new(new HashSet<(string ToolId, string ToolVersion)> { (toolId, toolVersion) });

    private static JsonObject Outcome(string toolId, string toolVersion) => new()
    {
        ["schema_version"] = 1,
        ["tool_id"] = toolId,
        ["tool_version"] = toolVersion,
        ["status"] = "failed",
        ["reason_code"] = "write_failed",
    };

    [Fact]
    public void Parse_round_trips_only_bounded_metadata()
    {
        var outcome = WorkerActionOutcome.Parse(
            new JsonObject
            {
                ["schema_version"] = 1,
                ["tool_id"] = "system.volume",
                ["tool_version"] = "1.0.0",
                ["status"] = "failed",
                ["reason_code"] = "write_failed",
            },
            VolumeOnly);

        Assert.True(outcome.ShouldRetain);
        Assert.Equal("write_failed", outcome.ToJson()["reason_code"]!.GetValue<string>());
        Assert.Equal(
            ["reason_code", "schema_version", "status", "tool_id", "tool_version"],
            outcome.ToJson().Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Successful_outcome_is_not_retained()
    {
        var outcome = WorkerActionOutcome.Parse(
            new JsonObject
            {
                ["schema_version"] = 1,
                ["tool_id"] = "system.volume",
                ["tool_version"] = "1.0.0",
                ["status"] = "succeeded",
                ["reason_code"] = null,
            },
            VolumeOnly);

        Assert.False(outcome.ShouldRetain);
        Assert.Null(outcome.ReasonCode);
    }

    [Fact]
    public void Parse_accepts_any_advertised_tool_identity()
    {
        var outcome = WorkerActionOutcome.Parse(
            new JsonObject
            {
                ["schema_version"] = 1,
                ["tool_id"] = "files.edit",
                ["tool_version"] = "1.2.3",
                ["status"] = "failed",
                ["reason_code"] = "edit_not_unique",
            },
            Advertised("files.edit", "1.2.3"));

        Assert.Equal("files.edit", outcome.ToolId);
        Assert.Equal("1.2.3", outcome.ToolVersion);
        Assert.True(outcome.ShouldRetain);
    }

    [Fact]
    public void Parse_rejects_ids_missing_from_the_advertised_snapshot()
    {
        var error = Assert.Throws<WorkerProtocolException>(() =>
            WorkerActionOutcome.Parse(Outcome("app.open", "1.0.0"), VolumeOnly));

        Assert.Equal("invalid_action_outcome", error.Code);
    }

    [Fact]
    public void Parse_rejects_unadvertised_versions_of_advertised_tools()
    {
        var error = Assert.Throws<WorkerProtocolException>(() =>
            WorkerActionOutcome.Parse(Outcome("system.volume", "2.0.0"), VolumeOnly));

        Assert.Equal("invalid_action_outcome", error.Code);
    }

    [Fact]
    public void Parse_rejects_an_empty_tool_id()
    {
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerActionOutcome.Parse(Outcome("", "1.0.0"), VolumeOnly));
    }

    [Fact]
    public void Parse_rejects_a_whitespace_tool_id()
    {
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerActionOutcome.Parse(Outcome(" ", "1.0.0"), VolumeOnly));
    }

    [Fact]
    public void Parse_rejects_an_oversized_tool_id()
    {
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerActionOutcome.Parse(Outcome(new string('a', 129), "1.0.0"), VolumeOnly));
    }

    [Fact]
    public void Parse_rejects_an_oversized_tool_version()
    {
        Assert.Throws<WorkerProtocolException>(() =>
            WorkerActionOutcome.Parse(Outcome("system.volume", new string('a', 65)), VolumeOnly));
    }

    [Fact]
    public void Parse_without_a_snapshot_validates_structure_only()
    {
        var outcome = WorkerActionOutcome.Parse(Outcome("app.open", "1.0.0"), null);

        Assert.Equal("app.open", outcome.ToolId);
        Assert.Equal("1.0.0", outcome.ToolVersion);
    }

    public static TheoryData<JsonNode> InvalidOutcomes => new()
    {
        new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "failed",
            ["reason_code"] = "write_failed",
            ["output"] = new JsonObject { ["level"] = 2 },
        },
        new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "failed",
            ["reason_code"] = null,
        },
        new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "failed",
            ["reason_code"] = new string('a', 65),
        },
        new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "succeeded",
            ["reason_code"] = "write_failed",
        },
        new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "unknown",
            ["reason_code"] = "write_failed",
        },
        new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "app.open",
            ["tool_version"] = "1.0.0",
            ["status"] = "failed",
            ["reason_code"] = "write_failed",
        },
        new JsonObject
        {
            ["schema_version"] = "one",
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "failed",
            ["reason_code"] = "write_failed",
        },
        new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = new JsonArray("failed"),
            ["reason_code"] = "write_failed",
        },
        new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "failed",
            ["reason_code"] = 7,
        },
    };

    [Theory]
    [MemberData(nameof(InvalidOutcomes))]
    public void Parse_rejects_malformed_values_with_one_stable_code(JsonNode payload)
    {
        var error = Assert.Throws<WorkerProtocolException>(() =>
            WorkerActionOutcome.Parse(payload, VolumeOnly));

        Assert.Equal("invalid_action_outcome", error.Code);
    }
}
