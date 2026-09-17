using System.Text.Json;
using RuAgent.Broker.Runtime;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;
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
        var scopes = new HashSet<string>(["workspace.write"], StringComparer.Ordinal);
        var skills = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(session.Open("session-1", scopes, skills));
        Assert.False(session.Open("session-2", scopes, skills));

        session.Close("session-1");

        Assert.True(session.Open("session-2", scopes, skills));
    }

    [Fact]
    public void Closing_a_session_drops_only_its_own_grants()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var grantedDirectory = Path.Combine(fixture.Root, "external");
        Directory.CreateDirectory(grantedDirectory);
        Assert.True(session.Open("broker-owner"));
        session.DirectoryGrants.Add(
            "agent-session",
            RuAgent.Broker.Policy.CanonicalDirectoryIdentity.CreateExisting(grantedDirectory));
        session.DirectoryGrants.Add(
            "agent-other",
            RuAgent.Broker.Policy.CanonicalDirectoryIdentity.CreateExisting(grantedDirectory));

        session.Close("agent-session");

        Assert.False(session.DirectoryGrants.Contains("agent-session", grantedDirectory));
        Assert.True(session.DirectoryGrants.Contains("agent-other", grantedDirectory));
        Assert.True(session.Owns("broker-owner"));
    }

    [Fact]
    public void V2_run_open_requires_valid_lease_before_advertising_capabilities()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var snapshot = LeaseTestSnapshots.StandardEmpty();
        var lease = BrokerRunLease.CreateValidated(
            "run_11111111111111111111111111111111",
            "session-1",
            LeaseTestSnapshots.WorkspaceId,
            Path.GetTempPath(),
            LeaseTestSnapshots.VolumeSerial,
            LeaseTestSnapshots.RootFileId,
            LeaseTestSnapshots.DisplayHash,
            "standard",
            1,
            RuAgent.Protocol.Harness.BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5),
            snapshot);
        Assert.True(session.TryOpenRunLease(lease, out var error));
        Assert.Null(error);
        Assert.NotNull(session.ActiveRunLease);
        Assert.Equal(
            new[] { "bash", "edit", "glob", "grep", "help", "list", "read", "web_fetch", "web_search", "write" },
            session.ActiveRunLease!.AdvertisedCapabilities.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void V2_run_leases_coexist_across_sessions()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        Assert.True(session.Open("session-owner"));

        var lease = CreateLease(
            "run_foreign111111111111111111111111",
            "session-foreign",
            LeaseTestSnapshots.WorkspaceId,
            LeaseTestSnapshots.DisplayHash,
            Path.GetTempPath(),
            LeaseTestSnapshots.StandardEmpty());

        Assert.True(session.TryOpenRunLease(lease, out var error));
        Assert.Null(error);
        Assert.True(session.Owns("session-owner"));
        Assert.True(session.Owns("session-foreign"));
    }

    [Fact]
    public void V2_identity_hash_mismatch_fails_closed()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var ex = Assert.Throws<AgentProtocolException>(() => BrokerRunLease.CreateValidated(
            "run_11111111111111111111111111111111",
            "session-1",
            LeaseTestSnapshots.WorkspaceId,
            Path.GetTempPath(),
            LeaseTestSnapshots.VolumeSerial,
            LeaseTestSnapshots.RootFileId,
            "invalid-hash",
            "standard",
            1,
            new string('b', 64),
            DateTimeOffset.UtcNow.AddMinutes(5),
            LeaseTestSnapshots.StandardEmpty()));
        Assert.Equal("invalid_display_hash", ex.Code);
    }

    [Fact]
    public void V2_lease_expired_fails_closed()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var snapshot = LeaseTestSnapshots.StandardEmpty();
        var ex = Assert.Throws<AgentProtocolException>(() => BrokerRunLease.CreateValidated(
            "run_11111111111111111111111111111111",
            "session-1",
            LeaseTestSnapshots.WorkspaceId,
            Path.GetTempPath(),
            LeaseTestSnapshots.VolumeSerial,
            LeaseTestSnapshots.RootFileId,
            LeaseTestSnapshots.DisplayHash,
            "standard",
            1,
            RuAgent.Protocol.Harness.BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            DateTimeOffset.UtcNow.AddSeconds(-10),
            snapshot));
        Assert.Equal("lease_expired", ex.Code);
    }

    [Fact]
    public void Phase_one_snapshot_advertises_every_canonical_tool()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var lease = CreateLease(
            "run_22222222222222222222222222222222",
            "session-2",
            "ws_22222222222222222222222222222222",
            new string('c', 64),
            Path.GetTempPath(),
            LeaseTestSnapshots.StandardEmpty());
        Assert.Equal(
            new[] { "bash", "edit", "glob", "grep", "help", "list", "read", "web_fetch", "web_search", "write" },
            lease.AdvertisedCapabilities.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void V2_lease_requires_snapshot_hash_to_match_the_carried_rules()
    {
        var snapshot = LeaseTestSnapshots.StandardEmpty(version: 3);
        var ex = Assert.Throws<AgentProtocolException>(() => CreateLease(
            "run_aaaa1111111111111111111111111111",
            "session-hash",
            LeaseTestSnapshots.WorkspaceId,
            LeaseTestSnapshots.DisplayHash,
            Path.GetTempPath(),
            snapshot,
            snapshotHash: new string('e', 64)));
        Assert.Equal("snapshot_hash_mismatch", ex.Code);
    }

    [Fact]
    public void V2_lease_rejects_snapshot_identity_that_differs_from_the_lease_identity()
    {
        var snapshot = LeaseTestSnapshots.StandardEmpty();
        var ex = Assert.Throws<AgentProtocolException>(() => CreateLease(
            "run_bbbb2222222222222222222222222222",
            "session-moved",
            "ws_99999999999999999999999999999999",
            LeaseTestSnapshots.DisplayHash,
            Path.GetTempPath(),
            snapshot));
        Assert.Equal("workspace_identity_mismatch", ex.Code);
    }

    [Fact]
    public void Rebind_increments_binding_version()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var first = CreateLease(
            "run_cccc3333333333333333333333333333",
            "session-rebind",
            LeaseTestSnapshots.WorkspaceId,
            LeaseTestSnapshots.DisplayHash,
            Path.GetTempPath(),
            LeaseTestSnapshots.StandardEmpty(version: 1));
        Assert.True(session.TryOpenRunLease(first, out _));
        Assert.Equal(1, session.ActiveRunLease!.BindingVersion);

        var second = CreateLease(
            first.RunId,
            first.SessionId,
            LeaseTestSnapshots.WorkspaceId,
            LeaseTestSnapshots.DisplayHash,
            first.CanonicalRoot,
            LeaseTestSnapshots.StandardEmpty(version: 2));
        Assert.True(session.TryRebindRunPolicy(second, out var rebindError));
        Assert.Null(rebindError);
        Assert.Equal(2, session.ActiveRunLease!.BindingVersion);
        Assert.Equal(2, session.ActiveRunLease.Snapshot.Version);
    }

    [Fact]
    public void Rebind_requires_a_monotonically_newer_snapshot_version()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var root = Path.Combine(Path.GetTempPath(), "ru-rebind-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = CreateLease(
                "run_dddd4444444444444444444444444444",
                "session-monotonic",
                LeaseTestSnapshots.WorkspaceId,
                LeaseTestSnapshots.DisplayHash,
                root,
                LeaseTestSnapshots.StandardEmpty(version: 5));
            Assert.True(session.TryOpenRunLease(first, out _));

            var sameVersion = CreateLease(
                first.RunId,
                first.SessionId,
                LeaseTestSnapshots.WorkspaceId,
                LeaseTestSnapshots.DisplayHash,
                root,
                LeaseTestSnapshots.StandardEmpty(version: 5));
            Assert.False(session.TryRebindRunPolicy(sameVersion, out var sameError));
            Assert.Equal("rebind_not_monotonic", sameError);

            var older = CreateLease(
                first.RunId,
                first.SessionId,
                LeaseTestSnapshots.WorkspaceId,
                LeaseTestSnapshots.DisplayHash,
                root,
                LeaseTestSnapshots.StandardEmpty(version: 4));
            Assert.False(session.TryRebindRunPolicy(older, out var olderError));
            Assert.Equal("rebind_not_monotonic", olderError);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Rebind_rejects_a_moved_workspace_identity()
    {
        using var fixture = DescriptorFixture.Create();
        var session = BrokerSession.Load(fixture.DescriptorPath, fixture.Now);
        var first = CreateLease(
            "run_eeee5555555555555555555555555555",
            "session-moved-rebind",
            LeaseTestSnapshots.WorkspaceId,
            LeaseTestSnapshots.DisplayHash,
            Path.GetTempPath(),
            LeaseTestSnapshots.StandardEmpty(version: 1));
        Assert.True(session.TryOpenRunLease(first, out _));

        var movedRoot = Path.Combine(Path.GetTempPath(), "ru-moved-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(movedRoot);
        try
        {
            var moved = CreateLease(
                first.RunId,
                first.SessionId,
                LeaseTestSnapshots.WorkspaceId,
                LeaseTestSnapshots.DisplayHash,
                movedRoot,
                LeaseTestSnapshots.StandardEmpty(version: 2));
            Assert.False(session.TryRebindRunPolicy(moved, out var error));
            Assert.Equal("workspace_changed", error);
        }
        finally
        {
            Directory.Delete(movedRoot, recursive: true);
        }
    }

    [Fact]
    public void Policy_context_factory_roots_relative_paths_at_the_lease_root_not_the_environment()
    {
        var environmentRoot = Path.Combine(Path.GetTempPath(), "ru-env-root-" + Guid.NewGuid().ToString("N"));
        var leaseRoot = Path.Combine(Path.GetTempPath(), "ru-lease-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(environmentRoot);
        Directory.CreateDirectory(leaseRoot);
        File.WriteAllText(Path.Combine(leaseRoot, "probe.txt"), "lease");
        File.WriteAllText(Path.Combine(environmentRoot, "probe.txt"), "environment");
        var previousEnvironment = Environment.GetEnvironmentVariable("RU_WORKSPACE_ROOT");
        Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", environmentRoot);
        try
        {
            var lease = CreateLease(
                "run_ffff6666666666666666666666666666",
                "session-root",
                LeaseTestSnapshots.WorkspaceId,
                LeaseTestSnapshots.DisplayHash,
                leaseRoot,
                LeaseTestSnapshots.StandardEmpty());
            var factory = new RuAgent.Broker.Policy.PolicyContextFactory(lease);
            var context = factory.Create("read", "1.0.0",
                new System.Text.Json.Nodes.JsonObject { ["path"] = "probe.txt" });

            Assert.True(context.Target!.IsInsideWorkspace);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(leaseRoot, "probe.txt")).ToLowerInvariant(),
                context.Target.CanonicalPath.ToLowerInvariant());
            Assert.NotEqual(
                Path.GetFullPath(Path.Combine(environmentRoot, "probe.txt")).ToLowerInvariant(),
                context.Target.CanonicalPath.ToLowerInvariant());
        }
        finally
        {
            Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", previousEnvironment);
            Directory.Delete(environmentRoot, recursive: true);
            Directory.Delete(leaseRoot, recursive: true);
        }
    }

    private static BrokerRunLease CreateLease(
        string runId,
        string sessionId,
        string workspaceId,
        string displayHash,
        string canonicalRoot,
        BrokerPolicySnapshotWire snapshot,
        long bindingVersion = 1,
        string? snapshotHash = null) =>
        BrokerRunLease.CreateValidated(
            runId,
            sessionId,
            workspaceId,
            canonicalRoot,
            LeaseTestSnapshots.VolumeSerial,
            LeaseTestSnapshots.RootFileId,
            displayHash,
            snapshot.Preset,
            snapshot.Version,
            snapshotHash ?? RuAgent.Protocol.Harness.BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            DateTimeOffset.UtcNow.AddMinutes(5),
            snapshot,
            bindingVersion);

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

internal static class LeaseTestSnapshots
{
    public const string WorkspaceId = "ws_22222222222222222222222222222222";
    public const string VolumeSerial = "VOL-SESS";
    public const string RootFileId = "fid-sess";
    public const string DisplayHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    public const string CreatedAt = "2026-08-24T00:00:00.0000000+00:00";

    public static BrokerPolicySnapshotWire StandardEmpty(long version = 1) => new(
        version,
        "standard",
        new BrokerPolicyWorkspaceIdentityWire(WorkspaceId, VolumeSerial, RootFileId, DisplayHash),
        [],
        CreatedAt);
}

