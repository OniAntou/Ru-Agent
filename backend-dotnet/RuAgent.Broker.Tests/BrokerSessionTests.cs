using System.Text.Json;
using RuAgent.Broker.Runtime;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class BrokerSessionTests
{
    [Fact]
    public void Load_accepts_a_loopback_descriptor_under_its_session_root()
    {
        using var fixture = DescriptorFixture.Create();

        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);

        Assert.Equal(fixture.Descriptor.BaseUrl, session.Descriptor.BaseUrl);
        Assert.Equal(fixture.Descriptor.AuthToken, session.Descriptor.AuthToken);
        Assert.Equal(Path.Combine(Path.GetFullPath(fixture.Root), "session"), session.Descriptor.SessionRoot);
    }

    [Theory]
    [InlineData("missing_token")]
    [InlineData("root_escape")]
    [InlineData("expired")]
    public void Load_rejects_invalid_descriptors(string scenario)
    {
        using var fixture = DescriptorFixture.Create(scenario);

        var error = Assert.Throws<BrokerSessionException>(
            () => BrokerSession.Load(fixture.DescriptorPath, fixture.Now));

        Assert.Equal(scenario, error.Code);
    }

    [Fact]
    public void A_different_owner_must_wait_until_the_active_session_closes()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var scopes = new HashSet<string>(["system.write"], StringComparer.Ordinal);
        var skills = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(session.Open("session-1", scopes, skills));
        Assert.False(session.Open("session-2", scopes, skills));

        session.Close("session-1");

        Assert.True(session.Open("session-2", scopes, skills));
    }

    [Fact]
    public void V2_run_open_requires_valid_lease_before_advertising_capabilities()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var lease = BrokerRunLease.CreateValidated(
            "run_11111111111111111111111111111111",
            "session-1",
            "ws_11111111111111111111111111111111",
            Path.GetTempPath(),
            "VOL-1234",
            "fid-1234",
            new string('a', 64),
            "standard",
            1,
            new string('b', 64),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.True(session.TryOpenRunLease(lease, out var error));
        Assert.Null(error);
        Assert.NotNull(session.ActiveRunLease);
        Assert.Equal(new[] { "files.list", "files.read" }, session.ActiveRunLease!.AdvertisedCapabilities.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void V2_identity_hash_mismatch_fails_closed()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var ex = Assert.Throws<AgentProtocolException>(() => BrokerRunLease.CreateValidated(
            "run_11111111111111111111111111111111",
            "session-1",
            "ws_11111111111111111111111111111111",
            Path.GetTempPath(),
            "VOL-1234",
            "fid-1234",
            "invalid-hash",
            "standard",
            1,
            new string('b', 64),
            DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.Equal("invalid_display_hash", ex.Code);
    }

    [Fact]
    public void V2_lease_expired_fails_closed()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var ex = Assert.Throws<AgentProtocolException>(() => BrokerRunLease.CreateValidated(
            "run_11111111111111111111111111111111",
            "session-1",
            "ws_11111111111111111111111111111111",
            Path.GetTempPath(),
            "VOL-1234",
            "fid-1234",
            new string('a', 64),
            "standard",
            1,
            new string('b', 64),
            DateTimeOffset.UtcNow.AddSeconds(-10)));
        Assert.Equal("lease_expired", ex.Code);
    }

    [Fact]
    public void Phase_one_snapshot_advertises_only_list_and_read()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var lease = BrokerRunLease.CreateValidated(
            "run_22222222222222222222222222222222",
            "session-2",
            "ws_22222222222222222222222222222222",
            Path.GetTempPath(),
            "VOL-5678",
            "fid-5678",
            new string('c', 64),
            "standard",
            1,
            new string('d', 64),
            DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(new[] { "files.list", "files.read" }, lease.AdvertisedCapabilities.OrderBy(x => x).ToArray());
        Assert.False(lease.AdvertisedCapabilities.Contains("files.write"));
        Assert.False(lease.AdvertisedCapabilities.Contains("terminal.run"));
    }

    private sealed class DescriptorFixture : IDisposable
    {
        private DescriptorFixture(
            string root,
            string descriptorPath,
            BrokerSessionDescriptor descriptor,
            DateTimeOffset now)
        {
            Root = root;
            DescriptorPath = descriptorPath;
            Descriptor = descriptor;
            Now = now;
        }

        public string Root { get; }
        public string DescriptorPath { get; }
        public BrokerSessionDescriptor Descriptor { get; }
        public DateTimeOffset Now { get; }

        public static DescriptorFixture Create(string? scenario = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-agent-session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            var descriptorPath = Path.Combine(root, "broker.json");
            var now = DateTimeOffset.UtcNow;
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:18086",
                scenario == "missing_token" ? string.Empty : "broker-token",
                1,
                scenario == "root_escape" ? Path.Combine(root, "..", "outside") : sessionRoot,
                scenario == "expired" ? now.AddSeconds(-1) : now.AddMinutes(5));
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));
            return new DescriptorFixture(root, descriptorPath, descriptor, now);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
