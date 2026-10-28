using RuAgent.Broker.Runtime;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class BrokerSessionTests
{
    /// <summary>
    /// In-process runtime sessions are descriptor-free: no broker.json, no
    /// loopback base URL, no bearer token — only the root and lifetime are
    /// real. Lease, scope and close behavior must stay exact.
    /// </summary>
    [Fact]
    public void CreateForHost_opens_a_run_lease_and_closes_without_a_descriptor_file()
    {
        using var fixture = SessionFixture.Create();
        var session = fixture.Session;

        Assert.False(session.IsReady);
        Assert.Equal(Path.GetFullPath(fixture.Root), session.SessionRoot);
        Assert.True(session.Open("session-host", new HashSet<string>(["workspace.write"], StringComparer.Ordinal)));
        Assert.Equal(new[] { "workspace.write" }, session.ScopesFor("session-host").ToArray());

        var lease = CreateLease(
            "run_11111111111111111111111111111111",
            "session-host",
            LeaseTestSnapshots.WorkspaceId,
            LeaseTestSnapshots.DisplayHash,
            Path.GetTempPath(),
            LeaseTestSnapshots.StandardEmpty());
        Assert.True(session.TryOpenRunLease(lease, out var leaseError));
        Assert.Null(leaseError);
        Assert.Equal(lease.RunId, session.ActiveRunLease!.RunId);

        Assert.True(session.TryCloseRun(lease.RunId));
        Assert.Null(session.ActiveRunLease);
        Assert.True(session.Owns("session-host"));

        session.Close("session-host");

        Assert.False(session.IsReady);
        Assert.False(session.Owns("session-host"));
    }

    [Fact]
    public void CreateForHost_applies_advertised_capabilities_and_fails_closed_on_bad_inputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-host-session-" + Guid.NewGuid().ToString("N"));

        var rootEscape = Assert.Throws<BrokerSessionException>(
            () => BrokerSession.CreateForHost(
                "relative" + Path.DirectorySeparatorChar + "session",
                DateTimeOffset.UtcNow.AddMinutes(30)));
        Assert.Equal("root_escape", rootEscape.Code);

        var expired = Assert.Throws<BrokerSessionException>(
            () => BrokerSession.CreateForHost(root, DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.Equal("expired", expired.Code);

        var session = BrokerSession.CreateForHost(
            root,
            DateTimeOffset.UtcNow.AddMinutes(30),
            advertisedCapabilities: ["read"]);
        Assert.Equal(new[] { "read" }, session.AdvertisedCapabilities);
    }

    [Fact]
    public void A_different_owner_must_wait_until_the_active_session_closes()
    {
        using var fixture = SessionFixture.Create();
        var session = fixture.Session;
        var scopes = new HashSet<string>(["workspace.write"], StringComparer.Ordinal);

        Assert.True(session.Open("session-1", scopes));
        Assert.False(session.Open("session-2", scopes));

        session.Close("session-1");

        Assert.True(session.Open("session-2", scopes));
    }

    [Fact]
    public void Closing_a_session_drops_only_its_own_grants()
    {
        using var fixture = SessionFixture.Create();
        var session = fixture.Session;
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
        using var fixture = SessionFixture.Create();
        var session = fixture.Session;
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
            AgentToolDefinitionCatalog.Get().Where(d => d.ToolId != "ask_user")
                .Select(d => d.ToolId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            session.ActiveRunLease!.AdvertisedCapabilities.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void V2_run_leases_coexist_across_sessions()
    {
        using var fixture = SessionFixture.Create();
        var session = fixture.Session;
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
        var lease = CreateLease(
            "run_22222222222222222222222222222222",
            "session-2",
            "ws_22222222222222222222222222222222",
            new string('c', 64),
            Path.GetTempPath(),
            LeaseTestSnapshots.StandardEmpty());
        Assert.Equal(
            AgentToolDefinitionCatalog.Get().Where(d => d.ToolId != "ask_user")
                .Select(d => d.ToolId).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
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
        using var fixture = SessionFixture.Create();
        var session = fixture.Session;
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
        using var fixture = SessionFixture.Create();
        var session = fixture.Session;
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
        using var fixture = SessionFixture.Create();
        var session = fixture.Session;
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

    /// <summary>
    /// A descriptor-free session over a throwaway root, disposed with its root.
    /// </summary>
    private sealed class SessionFixture : IDisposable
    {
        private SessionFixture(string root, BrokerSession session)
        {
            Root = root;
            Session = session;
        }

        public string Root { get; }
        public BrokerSession Session { get; }

        public static SessionFixture Create(DateTimeOffset? now = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-agent-session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var effectiveNow = now ?? DateTimeOffset.UtcNow;
            return new SessionFixture(
                root,
                BrokerSession.CreateForHost(root, effectiveNow.AddMinutes(5), now: effectiveNow));
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
