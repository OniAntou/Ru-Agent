using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Desktop;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class VolumeToolsTests
{
    [Fact]
    public void Set_uses_one_disposable_endpoint_and_returns_verified_proof()
    {
        var controller = new FakeVolumeController(level: 40);

        var outcome = new VolumeTools(controller).Execute(new JsonObject
        {
            ["operation"] = "set",
            ["level"] = 2,
        });

        Assert.Equal(ToolResultStatuses.Succeeded, outcome.Status);
        Assert.Equal(ToolVerificationStates.Verified, outcome.VerificationState);
        Assert.Equal("default_render_multimedia", outcome.EndpointRole);
        Assert.Matches("^[a-f0-9]{16}$", outcome.EndpointHash!);
        Assert.Equal("set", outcome.Output["operation"]!.GetValue<string>());
        Assert.Equal(2, outcome.Output["level"]!.GetValue<int>());
        Assert.False(outcome.Output.ContainsKey("verified"));
        Assert.Equal(1, controller.OpenCount);
        Assert.Equal(1, controller.DisposeCount);
        Assert.Equal(2, controller.ReadCount);
        Assert.Equal([("level", 2)], controller.Writes);
    }

    [Fact]
    public void Readback_mismatch_never_returns_success()
    {
        var outcome = new VolumeTools(new FakeVolumeController(40, applyWrites: false))
            .Execute(new JsonObject { ["operation"] = "set", ["level"] = 2 });

        Assert.Equal(ToolResultStatuses.Failed, outcome.Status);
        Assert.Equal("readback_mismatch", outcome.ErrorCode);
        Assert.Equal(ToolVerificationStates.Failed, outcome.VerificationState);
        Assert.Equal("default_render_multimedia", outcome.EndpointRole);
        Assert.Matches("^[a-f0-9]{16}$", outcome.EndpointHash!);
    }

    [Fact]
    public void Invalid_arguments_are_rejected_before_endpoint_acquisition()
    {
        var controller = new FakeVolumeController(level: 40);

        var outcome = new VolumeTools(controller).Execute(new JsonObject
        {
            ["operation"] = "set",
            ["level"] = 101,
        });

        Assert.Equal(ToolResultStatuses.Failed, outcome.Status);
        Assert.Equal("invalid_arguments", outcome.ErrorCode);
        Assert.Equal(0, controller.OpenCount);
    }

    [Fact]
    public void Increase_is_clamped_and_uses_the_legacy_default_amount()
    {
        var controller = new FakeVolumeController(level: 98);

        var outcome = new VolumeTools(controller).Execute(
            new JsonObject { ["operation"] = "increase" });

        Assert.Equal(ToolResultStatuses.Succeeded, outcome.Status);
        Assert.Equal(100, outcome.Output["level"]!.GetValue<int>());
        Assert.Equal([("level", 100)], controller.Writes);
    }

    internal sealed class FakeVolumeController(
        int level,
        bool muted = false,
        bool applyWrites = true) : IVolumeController
    {
        private VolumeState _state = new(level, muted);

        public int OpenCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int ReadCount { get; private set; }
        public List<(string Kind, int Value)> Writes { get; } = [];

        public IVolumeEndpoint OpenDefaultRenderEndpoint()
        {
            OpenCount++;
            return new FakeEndpoint(this, applyWrites);
        }

        private sealed class FakeEndpoint(FakeVolumeController owner, bool applyWrites) : IVolumeEndpoint
        {
            private bool _disposed;

            public string EndpointRole => "default_render_multimedia";
            public string EndpointHash => "0123456789abcdef";

            public VolumeState GetState()
            {
                owner.ReadCount++;
                return owner._state;
            }

            public void SetLevel(int value)
            {
                owner.Writes.Add(("level", value));
                if (applyWrites)
                {
                    owner._state = owner._state with { Level = value };
                }
            }

            public void SetMuted(bool value)
            {
                owner.Writes.Add(("muted", value ? 1 : 0));
                if (applyWrites)
                {
                    owner._state = owner._state with { Muted = value };
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner.DisposeCount++;
            }
        }
    }
}
