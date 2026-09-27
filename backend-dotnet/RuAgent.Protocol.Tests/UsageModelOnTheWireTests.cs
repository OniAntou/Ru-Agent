using System.Text.Json.Nodes;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Protocol.Tests;

/// <summary>
/// The model id on a usage report has to survive the worker's hop, not just the
/// adapter that produced it.
/// </summary>
/// <remarks>
/// This file exists because a field can be attached correctly at its source and still
/// be lost in transit. The worker writes its usage JSON by hand instead of calling the
/// record's own writer, and the receiver validates every key against an allowlist that
/// also assumed every value was a number. Both were written when usage was five
/// integer counts, so adding a sixth key that holds a name needed both sides changed
/// together. A unit test on the adapter alone would have passed while every real run
/// still reported no model at all, which is exactly what happened.
/// </remarks>
public sealed class UsageModelOnTheWireTests
{
    [Fact]
    public void A_report_that_names_its_model_is_accepted()
    {
        // Throws on an unaccepted shape, so reaching the end is the assertion.
        WorkerV2Contracts.ValidateStepDone(Done(usage: new JsonObject
        {
            ["input_tokens"] = 12,
            ["output_tokens"] = 5,
            ["total_tokens"] = 17,
            ["model"] = "vendor/paid",
        }));
    }

    [Fact]
    public void A_report_with_counts_only_is_still_accepted()
    {
        // A run recorded before the field existed has no model and must keep working:
        // it is a run nobody can price, not a malformed report.
        WorkerV2Contracts.ValidateStepDone(Done(usage: new JsonObject
        {
            ["input_tokens"] = 12,
            ["output_tokens"] = 5,
        }));
    }

    [Fact]
    public void A_model_that_is_not_a_usable_name_is_refused()
    {
        // The counts are non-negative for the same reason the model is bounded and
        // control-char free: the value is written into a stored payload and then looked
        // up in a price table, and neither side should have to defend itself.
        foreach (var model in new JsonNode?[]
        {
            new JsonObject(),
            12,
            string.Empty,
            new string('m', 257),
            "bad\nmodel",
        })
        {
            var usage = new JsonObject { ["input_tokens"] = 12 };
            usage["model"] = model;
            Assert.ThrowsAny<Exception>(() => WorkerV2Contracts.ValidateStepDone(Done(usage)));
        }
    }

    [Fact]
    public void An_unexpected_usage_key_is_still_refused()
    {
        // Widening the allowlist must not turn it into a free-for-all: an unknown field
        // is the shape of a payload from a different protocol version, and accepting it
        // would let a sender invent a field the receiver then silently drops.
        Assert.ThrowsAny<Exception>(() => WorkerV2Contracts.ValidateStepDone(Done(usage: new JsonObject
        {
            ["input_tokens"] = 12,
            ["cost_usd"] = 3,
        })));
    }

    [Fact]
    public void A_negative_count_is_still_refused_alongside_a_valid_model()
    {
        Assert.ThrowsAny<Exception>(() => WorkerV2Contracts.ValidateStepDone(Done(usage: new JsonObject
        {
            ["input_tokens"] = -1,
            ["model"] = "vendor/paid",
        })));
    }

    private static JsonObject Done(JsonObject usage) => new()
    {
        ["text"] = "done",
        ["tool_calls"] = new JsonArray(),
        ["finish_reason"] = "stop",
        ["prompt_receipt"] = new JsonObject
        {
            ["composer_version"] = "2.0.0",
            ["prompt_sha256"] = new string('a', 64),
            ["block_digests"] = new JsonArray(),
        },
        ["usage"] = usage,
    };
}
