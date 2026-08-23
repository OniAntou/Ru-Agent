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
