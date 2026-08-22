using RuAgent.Broker.Tools.Desktop;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class WindowsCoreAudioVolumeControllerTests
{
    private static readonly byte[] Salt = Enumerable.Repeat((byte)0x5a, 32).ToArray();

    [Fact]
    public void Successful_lease_releases_in_reverse_order_and_balances_com_on_same_thread()
    {
        var native = new FakeCoreAudioNativeApi();
        var controller = new WindowsCoreAudioVolumeController(native, Salt);

        using (var endpoint = controller.OpenDefaultRenderEndpoint())
        {
            Assert.Equal("default_render_multimedia", endpoint.EndpointRole);
            Assert.Matches("^[0-9a-f]{16}$", endpoint.EndpointHash);
            Assert.Equal(new VolumeState(40, false), endpoint.GetState());
        }

        Assert.Equal(new[] { "endpoint", "device", "enumerator" }, native.Releases);
        Assert.Equal(1, native.UninitializeCount);
        Assert.Equal(native.InitializeThreadId, native.UninitializeThreadId);
    }

    [Fact]
    public void Lease_disposal_is_idempotent()
    {
        var native = new FakeCoreAudioNativeApi();
        var endpoint = new WindowsCoreAudioVolumeController(native, Salt)
            .OpenDefaultRenderEndpoint();

        endpoint.Dispose();
        endpoint.Dispose();

        Assert.Equal(new[] { "endpoint", "device", "enumerator" }, native.Releases);
        Assert.Equal(1, native.UninitializeCount);
    }

    [Theory]
    [InlineData(AcquisitionFailure.Enumerator, new string[0])]
    [InlineData(AcquisitionFailure.Device, new[] { "enumerator" })]
    [InlineData(AcquisitionFailure.Endpoint, new[] { "device", "enumerator" })]
    [InlineData(AcquisitionFailure.EndpointId, new[] { "endpoint", "device", "enumerator" })]
    public void Partial_acquisition_failure_releases_only_acquired_resources(
        AcquisitionFailure failure,
        string[] expectedReleases)
    {
        var native = new FakeCoreAudioNativeApi { Failure = failure };
        var controller = new WindowsCoreAudioVolumeController(native, Salt);

        Assert.Throws<VolumeControllerException>(() => controller.OpenDefaultRenderEndpoint());

        Assert.Equal(expectedReleases, native.Releases);
        Assert.Equal(1, native.UninitializeCount);
    }

    [Fact]
    public void Changed_com_apartment_is_not_uninitialized_by_the_lease()
    {
        var native = new FakeCoreAudioNativeApi
        {
            InitializeResult = unchecked((int)0x80010106),
        };
        var controller = new WindowsCoreAudioVolumeController(native, Salt);

        using (controller.OpenDefaultRenderEndpoint())
        {
        }

        Assert.Equal(0, native.UninitializeCount);
    }

    public enum AcquisitionFailure
    {
        None,
        Enumerator,
        Device,
        Endpoint,
        EndpointId,
    }

    internal sealed class FakeCoreAudioNativeApi : ICoreAudioNativeApi
    {
        public int InitializeResult { get; init; }
        public AcquisitionFailure Failure { get; init; }
        public int InitializeThreadId { get; private set; }
        public int UninitializeThreadId { get; private set; }
        public int UninitializeCount { get; private set; }
        public List<string> Releases { get; } = [];

        public int InitializeCom()
        {
            InitializeThreadId = Environment.CurrentManagedThreadId;
            return InitializeResult;
        }

        public object CreateEnumerator()
        {
            ThrowAt(AcquisitionFailure.Enumerator);
            return "enumerator";
        }

        public object GetDefaultRenderEndpoint(object enumerator)
        {
            ThrowAt(AcquisitionFailure.Device);
            return "device";
        }

        public object ActivateEndpointVolume(object device)
        {
            ThrowAt(AcquisitionFailure.Endpoint);
            return "endpoint";
        }

        public string GetEndpointId(object device)
        {
            ThrowAt(AcquisitionFailure.EndpointId);
            return "raw-device-endpoint-id";
        }

        public VolumeState GetState(object endpoint) => new(40, false);

        public void SetLevel(object endpoint, int level)
        {
        }

        public void SetMuted(object endpoint, bool muted)
        {
        }

        public void Release(object resource) => Releases.Add((string)resource);

        public void UninitializeCom()
        {
            UninitializeCount++;
            UninitializeThreadId = Environment.CurrentManagedThreadId;
        }

        private void ThrowAt(AcquisitionFailure stage)
        {
            if (Failure == stage)
            {
                throw new InvalidOperationException("native acquisition failed");
            }
        }
    }
}
