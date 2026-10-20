using System.Text.Json;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentSubagentChangeModelTests
{
    private const string Root = "run_0123456789abcdef0123456789abcdef";

    [Fact]
    public void TryParse_reads_the_newest_attempt_of_every_child()
    {
        var frame = Frame(7, new
        {
            root_run_id = Root,
            root_version = 3,
            agents = new object[]
            {
                new
                {
                    agent_id = "review-1",
                    canceled = false,
                    attempts = new object[]
                    {
                        new { attempt_run_id = Root, attempt_number = 1, version = 1, status = "failed" },
                        new { attempt_run_id = Root, attempt_number = 2, version = 1, status = "running" },
                    },
                    results = Array.Empty<object>(),
                },
                new
                {
                    agent_id = "coder-2",
                    canceled = true,
                    attempts = new object[] { new { attempt_number = 1, status = "completed" } },
                    results = new object[] { new { result_version = 1, status = "completed" } },
                },
            },
            action_revisions = Array.Empty<object>(),
        });

        Assert.True(AgentSubagentChangeModel.TryParse(frame, out var change));
        Assert.NotNull(change);
        Assert.Equal(Root, change.RootRunId);
        Assert.Equal(7, change.TransientSequence);
        Assert.Collection(
            change.Agents,
            first =>
            {
                Assert.Equal("review-1", first.AgentId);
                Assert.Equal("running", first.Status);
                Assert.Equal(2, first.AttemptNumber);
                Assert.False(first.Canceled);
            },
            second =>
            {
                Assert.Equal("coder-2", second.AgentId);
                Assert.True(second.Canceled);
                Assert.Equal(1, second.ResultCount);
            });
    }

    [Fact]
    public void TryParse_reports_a_child_without_attempts_as_unavailable()
    {
        var frame = Frame(1, new { root_run_id = Root, agents = new object[] { new { agent_id = "review-1" } } });

        Assert.True(AgentSubagentChangeModel.TryParse(frame, out var change));
        Assert.Equal("unavailable", change!.Agents[0].Status);
    }

    [Fact]
    public void TryParse_accepts_an_empty_child_set()
    {
        Assert.True(AgentSubagentChangeModel.TryParse(Frame(1, new { root_run_id = Root, agents = Array.Empty<object>() }), out var change));
        Assert.Empty(change!.Agents);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"kind\":\"agent.usage_estimate\",\"data\":\"{}\"}")]
    [InlineData("{\"kind\":\"subagent.changed\"}")]
    [InlineData("{\"kind\":\"subagent.changed\",\"data\":\"\"}")]
    [InlineData("{\"kind\":\"subagent.changed\",\"data\":\"{\\\"agents\\\":[]}\"}")]
    [InlineData("{\"kind\":\"subagent.changed\",\"data\":\"{\\\"root_run_id\\\":\\\"nope\\\",\\\"agents\\\":[]}\"}")]
    [InlineData("{\"kind\":\"subagent.changed\",\"data\":\"{\\\"root_run_id\\\":\\\"run_0123456789abcdef0123456789abcdef\\\",\\\"agents\\\":[{\\\"agent_id\\\":\\\"bad id!\\\"}]}\"}")]
    public void TryParse_rejects_malformed_payloads(string data)
    {
        var frame = new SseFrame(null, "transient", data, true, 4);

        Assert.False(AgentSubagentChangeModel.TryParse(frame, out var change));
        Assert.Null(change);
    }

    [Fact]
    public void TryParse_rejects_durable_frames_and_a_missing_transient_sequence()
    {
        var data = JsonSerializer.Serialize(new { kind = "subagent.changed", data = JsonSerializer.Serialize(new { root_run_id = Root, agents = Array.Empty<object>() }) });

        Assert.False(AgentSubagentChangeModel.TryParse(new SseFrame(5, "transient", data, false, 5), out _));
        Assert.False(AgentSubagentChangeModel.TryParse(new SseFrame(null, "transient", data, true, null), out _));
    }

    [Fact]
    public void TryParse_rejects_an_unbounded_child_list()
    {
        var agents = Enumerable.Range(0, 65).Select(index => new { agent_id = $"child-{index}" }).ToArray();

        Assert.False(AgentSubagentChangeModel.TryParse(Frame(1, new { root_run_id = Root, agents }), out _));
    }

    private static SseFrame Frame(long sequence, object inner) =>
        new(null, "transient", JsonSerializer.Serialize(new { kind = "subagent.changed", data = JsonSerializer.Serialize(inner) }), true, sequence);
}
