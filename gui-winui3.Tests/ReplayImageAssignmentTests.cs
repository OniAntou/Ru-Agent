using System.Collections.ObjectModel;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>A reopened chat finds the images each user message carried.</summary>
public sealed class ReplayImageAssignmentTests
{
    private static readonly string SteerOne = "steer_" + new string('1', 32);
    private static readonly string SteerTwo = "steer_" + new string('2', 32);

    private static readonly AgentRunImagePayload[] RunImages =
    [
        new("payload_" + new string('a', 32)),
        new("payload_" + new string('b', 32), SteerOne),
        new("payload_" + new string('c', 32), SteerOne),
        new("payload_" + new string('d', 32), SteerTwo),
    ];

    [Fact]
    public void A_prompt_takes_the_images_without_a_steer_and_a_steer_takes_its_own()
    {
        var prompt = new ChatMessageItem { Role = "user", SourceRunId = "run_1" };
        var steer = new ChatMessageItem { Role = "user", SourceRunId = "run_1", SourceSteerId = SteerOne };

        Assert.Equal(["payload_" + new string('a', 32)], ReplayImageAssignment.PayloadsFor(prompt, RunImages));
        Assert.Equal(["payload_" + new string('b', 32), "payload_" + new string('c', 32)],
            ReplayImageAssignment.PayloadsFor(steer, RunImages));
    }

    [Theory]
    [InlineData("act_11111111111111111111111111111111_steer", "steer_11111111111111111111111111111111")]
    [InlineData("act_11111111111111111111111111111111", null)]
    [InlineData("act_XYZ_steer", null)]
    [InlineData(null, null)]
    public void A_steer_activity_names_its_steer(string? activityId, string? steerId)
    {
        Assert.Equal(steerId, ReplayImageAssignment.SteerIdFromActivityId(activityId));
    }

    [Fact]
    public void Only_readable_image_data_urls_decode()
    {
        var decoded = ReplayImageAssignment.DecodeDataUrl("data:image/png;base64,AQID");
        Assert.Equal("image/png", decoded!.Value.MimeType);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Value.Bytes);
        Assert.Null(ReplayImageAssignment.DecodeDataUrl("data:image/png;base64,***"));
        Assert.Null(ReplayImageAssignment.DecodeDataUrl("data:text/plain;base64,AQID"));
        Assert.Null(ReplayImageAssignment.DecodeDataUrl(null));
    }

    [Fact]
    public void A_replayed_steer_knows_which_steer_it_was()
    {
        var steer = new AgentActivityItem(new AgentActivityModel("act_" + new string('1', 32) + "_steer",
            AgentActivityKind.Steer, AgentActivityState.Completed, null, null, null, null, null, null, null, null,
            "succeeded", null, null, null, Detail: AgentSteerText.WithImages("look", 2)));
        var message = new ChatMessageItem
        {
            Role = "assistant", SourceRunId = "run_1",
            Segments = new ObservableCollection<AssistantSegment> { new ActivitySegment(steer.ActivityId, steer), new TextSegment("Done.") },
        };

        var said = TranscriptSteerSplitter.Split(message)[0];

        Assert.Equal(SteerOne, said.SourceSteerId);
        Assert.Equal("run_1", said.SourceRunId);
        Assert.Equal(2, said.MissingImageCount);
    }
}
