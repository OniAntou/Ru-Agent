using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using BrokerRunLease = RuAgent.Broker.Runtime.BrokerRunLease;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// Golden locks for the security columns the Broker enforces. Literals were
/// generated from the 9341402b Broker before the descriptor table existed. A change to
/// any manifest column, to a tool's session grant, or to the grant union must
/// edit these rows deliberately, in the same reviewed change.
/// </summary>
public sealed class ToolManifestGoldenLockTests
{
    private sealed record ManifestLock(
        string Id,
        string Version,
        string Adapter,
        RiskClass Risk,
        string Scopes,
        bool RequiresApproval,
        bool WorkspaceRequired,
        int MaxOutputBytes,
        int TimeoutMs,
        string TrustClass,
        bool NetworkAllowed);

    private sealed record SessionLock(string ToolId, string Scopes);

    // Sorted by tool id as the manifest registry lists them; scopes are a sorted comma-separated set.
    // Keep the literal rows in registry order.
    private static readonly ManifestLock[] ManifestLocks =
    [
        new("agent_apply", "1.0.0", "builtin", RiskClass.Reversible, "workspace.write", true, true, 4194304, 30000, "builtin", false),
        new("agent_cancel", "1.0.0", "builtin", RiskClass.Read, "system.read", false, false, 4194304, 30000, "builtin", false),
        new("agent_send", "1.0.0", "builtin", RiskClass.Read, "system.read", false, false, 4194304, 30000, "builtin", false),
        new("agent_spawn", "1.0.0", "builtin", RiskClass.Read, "system.read", false, false, 4194304, 30000, "builtin", false),
        new("agent_wait", "1.0.0", "builtin", RiskClass.Read, "system.read", false, false, 4194304, 30000, "builtin", false),
        new("apply_patch", "1.0.0", "desktop", RiskClass.Reversible, "workspace.write", true, true, 4194304, 30000, "builtin", false),
        new("computer", "1.0.0", "desktop", RiskClass.External, "terminal.execute", true, false, 4194304, 30000, "builtin", false),
        new("edit_file", "1.0.0", "desktop", RiskClass.Reversible, "workspace.write", true, true, 4194304, 30000, "builtin", false),
        new("file_delete", "1.0.0", "desktop", RiskClass.Reversible, "workspace.write", true, true, 4194304, 30000, "builtin", false),
        new("file_restore", "1.0.0", "desktop", RiskClass.Reversible, "workspace.write", true, true, 4194304, 30000, "builtin", false),
        new("glob", "1.0.0", "builtin", RiskClass.Read, "system.read, workspace.read", false, false, 4194304, 30000, "builtin", false),
        new("grep", "1.0.0", "builtin", RiskClass.Read, "system.read, workspace.read", false, false, 4194304, 30000, "builtin", false),
        new("help", "1.0.0", "builtin", RiskClass.Read, "system.read", false, false, 4194304, 30000, "builtin", false),
        new("list", "1.0.0", "builtin", RiskClass.Read, "system.read, workspace.read", false, false, 4194304, 30000, "builtin", false),
        new("mcp", "1.0.0", "connector", RiskClass.External, "terminal.execute", true, false, 4194304, 30000, "builtin", false),
        new("powershell", "1.0.0", "terminal", RiskClass.Reversible, "terminal.execute", true, false, 4194304, 30000, "builtin", false),
        new("read", "1.0.0", "builtin", RiskClass.Read, "system.read", false, false, 4194304, 30000, "builtin", false),
        new("read_file", "1.0.0", "builtin", RiskClass.Read, "system.read", false, false, 32768, 30000, "builtin", false),
        new("run_code", "1.0.0", "builtin", RiskClass.Reversible, "system.read", false, false, 4194304, 30000, "builtin", false),
        new("run_process", "1.0.0", "terminal", RiskClass.Reversible, "terminal.execute", true, false, 4194304, 30000, "builtin", false),
        new("skill", "1.0.0", "builtin", RiskClass.Read, "system.read", false, false, 4194304, 30000, "builtin", false),
        new("terminal_read", "1.0.0", "terminal", RiskClass.Reversible, "terminal.execute", true, false, 4194304, 30000, "builtin", false),
        new("terminal_stop", "1.0.0", "terminal", RiskClass.Reversible, "terminal.execute", true, false, 4194304, 30000, "builtin", false),
        new("todo_write", "1.0.0", "builtin", RiskClass.Read, "system.read", false, false, 4194304, 30000, "builtin", false),
        new("web_fetch", "1.0.0", "web", RiskClass.External, "network.fetch", true, false, 4194304, 30000, "builtin", false),
        new("web_search", "1.0.0", "web", RiskClass.External, "network.search", true, false, 4194304, 30000, "builtin", false),
        new("write_file", "1.0.0", "desktop", RiskClass.Reversible, "workspace.write", true, true, 4194304, 30000, "builtin", false),
    ];

    // Each row is the grant one lease advertising only that tool produces. Every row equals the
    // tool's manifest scopes, except the two that add nothing: ask_user (host-intercepted, no
    // manifest) and the unknown-tool probe future_tool. An empty grant is the fail-closed result.
    // Changed at step 3 (default arm removed), from the previous workspace.read fallback:
    //   read, read_file, run_code: system.read + workspace.read -> system.read
    //   list: workspace.read -> system.read + workspace.read (manifest extra scope system.read)
    //   help, todo_write, skill, agent_spawn, agent_send, agent_wait, agent_cancel: workspace.read -> system.read
    //   agent_apply: workspace.read -> workspace.write (its manifest requires workspace.write)
    //   ask_user, future_tool: workspace.read -> none
    // The grant union over the full catalog is unchanged, as is the production lease, which always
    // advertises the whole bound catalog.
    private static readonly SessionLock[] SessionLocks =
    [
        new("read", "system.read"),
        new("list", "system.read, workspace.read"),
        new("help", "system.read"),
        new("todo_write", "system.read"),
        new("grep", "system.read, workspace.read"),
        new("glob", "system.read, workspace.read"),
        new("web_search", "network.search"),
        new("web_fetch", "network.fetch"),
        new("skill", "system.read"),
        new("ask_user", ""),
        new("mcp", "terminal.execute"),
        new("computer", "terminal.execute"),
        new("agent_spawn", "system.read"),
        new("agent_send", "system.read"),
        new("agent_wait", "system.read"),
        new("agent_cancel", "system.read"),
        new("agent_apply", "workspace.write"),
        new("run_code", "system.read"),
        new("apply_patch", "workspace.write"),
        new("read_file", "system.read"),
        new("write_file", "workspace.write"),
        new("edit_file", "workspace.write"),
        new("file_delete", "workspace.write"),
        new("file_restore", "workspace.write"),
        new("powershell", "terminal.execute"),
        new("run_process", "terminal.execute"),
        new("terminal_read", "terminal.execute"),
        new("terminal_stop", "terminal.execute"),
        new("future_tool", ""),
    ];

    // Grant union over the whole advertised catalog (ask_user excluded, as in the lease).
    private static readonly string[] SessionUnionLock =
    [
        "network.fetch",
        "network.search",
        "system.read",
        "terminal.execute",
        "workspace.read",
        "workspace.write",
    ];

    [Fact]
    public void Manifests_security_columns_are_locked()
    {
        var actual = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs()).Registry.List()
            .Select(binding => new ManifestLock(
                binding.Manifest.Id,
                binding.Manifest.Version,
                binding.Manifest.Adapter,
                binding.Manifest.Risk,
                string.Join(", ", binding.Manifest.Scopes.OrderBy(scope => scope, StringComparer.Ordinal)),
                binding.Manifest.RequiresApproval,
                binding.Manifest.WorkspaceRequired,
                binding.Manifest.MaxOutputBytes,
                binding.Manifest.TimeoutMs,
                binding.Manifest.TrustClass,
                binding.Manifest.NetworkAllowed))
            .ToArray();

        Assert.Equal(ManifestLocks, actual);
    }

    [Fact]
    public void Manifest_scopes_contain_the_protocol_scope_of_the_same_tool()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        foreach (var binding in catalog.Registry.List())
        {
            var definition = AgentToolDefinitionCatalog.Find(binding.Manifest.Id, binding.Manifest.Version);
            Assert.NotNull(definition);
            Assert.Contains(definition!.Scope, binding.Manifest.Scopes);
        }
    }

    [Fact]
    public void Catalog_binds_every_protocol_tool_except_the_host_intercepted_ask_user()
    {
        var bound = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs()).Registry.List()
            .Select(binding => binding.Manifest.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var expected = AgentToolDefinitionCatalog.Get()
            .Select(definition => definition.ToolId)
            .Where(id => id != "ask_user")
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, bound);
    }

    [Fact]
    public void Session_grant_per_advertised_tool_is_locked()
    {
        var actual = SessionLocks
            .Select(row => new SessionLock(row.ToolId, SessionScopesFor(row.ToolId)))
            .ToArray();

        Assert.Equal(SessionLocks, actual);
    }

    [Fact]
    public void Session_grant_for_each_bound_tool_equals_its_manifest_scopes()
    {
        var catalog = new ToolCatalog(ToolBindingRegistryTests.ExecutableStubs());
        foreach (var binding in catalog.Registry.List())
        {
            var manifestScopes = string.Join(", ", binding.Manifest.Scopes.OrderBy(scope => scope, StringComparer.Ordinal));

            Assert.Equal(manifestScopes, SessionScopesFor(binding.Manifest.Id));
        }
    }

    [Fact]
    public void Session_grant_union_over_the_full_catalog_is_locked()
    {
        var advertised = AgentToolDefinitionCatalog.Get()
            .Select(definition => definition.ToolId)
            .Where(id => id != "ask_user")
            .ToArray();

        Assert.Equal(SessionUnionLock, GrantUnion(advertised).OrderBy(scope => scope, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Trusted_adapter_set_is_locked()
    {
        var accepted = new[] { "builtin", "desktop", "terminal", "web", "connector" };
        foreach (var adapter in accepted)
        {
            var registry = new ToolRegistry();
            registry.Register(ProbeManifest(adapter));
        }

        var error = Assert.Throws<ArgumentException>(() => new ToolRegistry().Register(ProbeManifest("app.open")));
        Assert.Equal("invalid_tool_manifest", error.Message);
    }

    private static ToolManifest ProbeManifest(string adapter) => new(
        "read",
        "1.0.0",
        adapter,
        RiskClass.Read,
        new HashSet<string>([ToolScope.SystemRead], StringComparer.Ordinal),
        AgentToolDefinitionCatalog.Find("read", AgentToolDefinitionCatalog.CurrentVersion)!.InputSchema,
        new System.Text.Json.Nodes.JsonObject { ["type"] = "object" },
        30_000,
        4 * 1024 * 1024,
        "builtin");

    private static string SessionScopesFor(string toolId) =>
        string.Join(", ", GrantUnion([toolId]).OrderBy(scope => scope, StringComparer.Ordinal));

    private static IReadOnlySet<string> GrantUnion(string[] toolIds)
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string sessionId = "golden-session";
            var snapshot = LeaseTestSnapshots.StandardEmpty();
            var session = BrokerSession.CreateForHost(root, DateTimeOffset.UtcNow.AddMinutes(5), advertisedCapabilities: toolIds);
            var lease = BrokerRunLease.CreateValidated(
                "run_11111111111111111111111111111111",
                sessionId,
                LeaseTestSnapshots.WorkspaceId,
                root,
                LeaseTestSnapshots.VolumeSerial,
                LeaseTestSnapshots.RootFileId,
                LeaseTestSnapshots.DisplayHash,
                snapshot.Preset,
                snapshot.Version,
                BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
                DateTimeOffset.UtcNow.AddMinutes(5),
                snapshot,
                advertisedCapabilities: toolIds);
            Assert.True(session.TryOpenRunLease(lease, out var error), error);
            return session.ScopesFor(sessionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
