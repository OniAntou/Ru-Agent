using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools.Desktop;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class VolumeToolsTests
{
    [Fact]
    public void Set_reads_back_the_exact_requested_level()
    {
        var controller = new FakeVolumeController(level: 40);

        var output = new VolumeTools(controller).Execute(
            new JsonObject
            {
                ["operation"] = "set",
                ["level"] = 2,
            });

        Assert.Equal("set", output["operation"]!.GetValue<string>());
        Assert.Equal(2, output["level"]!.GetValue<int>());
        Assert.False(output["muted"]!.GetValue<bool>());
        Assert.True(output["verified"]!.GetValue<bool>());
        Assert.Equal([("level", 2)], controller.Writes);
        Assert.True(controller.Reads >= 2);
    }

    [Fact]
    public void Increase_is_clamped_and_uses_the_legacy_default_when_amount_is_missing()
    {
        var controller = new FakeVolumeController(level: 98);

        var output = new VolumeTools(controller).Execute(
            new JsonObject { ["operation"] = "increase" });

        Assert.Equal(100, output["level"]!.GetValue<int>());
        Assert.Equal([("level", 100)], controller.Writes);
    }

    [Fact]
    public void Invalid_arguments_are_rejected_before_the_controller_is_touched()
    {
        var controller = new FakeVolumeController(level: 40);

        Assert.Throws<VolumeToolException>(() => new VolumeTools(controller).Execute(
            new JsonObject
            {
                ["operation"] = "set",
                ["level"] = 101,
            }));

        Assert.Equal(0, controller.Reads);
        Assert.Empty(controller.Writes);
    }

    [Fact]
    public void Readback_mismatch_is_a_failed_tool_result()
    {
        var controller = new FakeVolumeController(level: 40, applyWrites: false);

        var error = Assert.Throws<VolumeToolException>(() => new VolumeTools(controller).Execute(
            new JsonObject
            {
                ["operation"] = "set",
                ["level"] = 2,
            }));

        Assert.Equal("readback_mismatch", error.Code);
        Assert.Equal("Volume change could not be verified.", error.SafeSummary);
    }

    private sealed class FakeVolumeController(int level, bool muted = false, bool applyWrites = true)
        : IVolumeController
    {
        public VolumeState State { get; private set; } = new(level, muted);
        public bool ApplyWrites { get; } = applyWrites;
        public int Reads { get; private set; }
        public List<(string Kind, int Value)> Writes { get; } = [];

        public VolumeState GetState()
        {
            Reads++;
            return State;
        }

        public void SetLevel(int value)
        {
            Writes.Add(("level", value));
            if (ApplyWrites)
            {
                State = State with { Level = value };
            }
        }

        public void SetMuted(bool value)
        {
            if (ApplyWrites)
            {
                State = State with { Muted = value };
            }
        }
    }
}
