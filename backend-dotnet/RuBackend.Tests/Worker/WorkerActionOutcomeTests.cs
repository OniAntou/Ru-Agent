using System.Text.Json.Nodes;
using RuBackend.Worker;
using Xunit;

namespace RuBackend.Tests.Worker;

public sealed class WorkerActionOutcomeTests
{
    [Fact]
    public void Parse_round_trips_only_bounded_metadata()
    {
        var outcome = WorkerActionOutcome.Parse(new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "failed",
            ["reason_code"] = "write_failed",
        });

        Assert.True(outcome.ShouldRetain);
        Assert.Equal("write_failed", outcome.ToJson()["reason_code"]!.GetValue<string>());
        Assert.Equal(
            ["reason_code", "schema_version", "status", "tool_id", "tool_version"],
            outcome.ToJson().Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Successful_outcome_is_not_retained()
    {
        var outcome = WorkerActionOutcome.Parse(new JsonObject
        {
            ["schema_version"] = 1,
            ["tool_id"] = "system.volume",
            ["tool_version"] = "1.0.0",
            ["status"] = "succeeded",
            ["reason_code"] = null,
        });

        Assert.False(outcome.ShouldRetain);
        Assert.Null(outcome.ReasonCode);
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
            WorkerActionOutcome.Parse(payload));

        Assert.Equal("invalid_action_outcome", error.Code);
    }
}
