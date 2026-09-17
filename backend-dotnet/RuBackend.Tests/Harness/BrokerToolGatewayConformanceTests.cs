extern alias Broker;

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Tests.Conformance;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Xunit;
using BrokerTestHostHost = Broker::RuAgent.Broker.Runtime.BrokerHost;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Conformance suite over a real loopback BrokerHost. Every case allocates a loopback
/// port and spawns real processes, so it shares the non-parallel process-heavy
/// collection with the other terminal-spawning suites.
/// </summary>
[Collection(nameof(TerminalProcessHeavyCollection))]
public sealed class BrokerToolGatewayConformanceTests : ToolGatewayConformance
{
    private readonly List<BrokerTestHost> _hosts = [];

    protected override bool StreamsToolOutputDeltas => false;

    protected override async Task<IToolGateway> CreateGatewayAsync(string workspaceRoot, string preset)
    {
        var host = await BrokerTestHost.StartAsync(workspaceRoot);
        _hosts.Add(host);
        const string sessionId = "session-conformance";
        var runId = "run_conf" + Guid.NewGuid().ToString("N")[..28];
        return new RuBackend.Harness.Broker.BrokerToolGateway(
            new Uri(host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/agent"),
            host.Token,
            sessionId,
            runId,
            () => BuildRunOpenPayload(runId, sessionId, workspaceRoot, preset));
    }

    protected override void DisposeGateway()
    {
        foreach (var host in _hosts)
        {
            try { host.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); } catch { }
        }

        _hosts.Clear();
    }

    private static JsonObject BuildRunOpenPayload(string runId, string sessionId, string canonicalRoot, string preset)
    {
        var snapshot = new BrokerPolicySnapshotWire(
            1,
            preset,
            new BrokerPolicyWorkspaceIdentityWire(
                "ws_11111111111111111111111111111111",
                "VOL-CONFORMANCE",
                "fid-conformance",
                new string('a', 64)),
            [],
            "2026-08-24T12:00:00.0000000+00:00");
        return new JsonObject
        {
            ["run_id"] = runId,
            ["session_id"] = sessionId,
            ["workspace_id"] = snapshot.Workspace.WorkspaceId,
            ["canonical_root"] = canonicalRoot,
            ["volume_serial"] = snapshot.Workspace.VolumeSerialNumber,
            ["root_file_id"] = snapshot.Workspace.RootFileId,
            ["display_hash"] = snapshot.Workspace.DisplayIdentityHash,
            ["preset"] = snapshot.Preset,
            ["snapshot_version"] = snapshot.Version,
            ["snapshot_hash"] = BrokerPolicyContracts.ComputeCanonicalSnapshotHash(snapshot),
            ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
            ["snapshot"] = BrokerPolicyContracts.WriteSnapshot(snapshot),
        };
    }

    private sealed class BrokerTestHost : IAsyncDisposable
    {
        private BrokerTestHost(string baseUrl, string token, BrokerTestHostHost host, string? previousWorkspaceRoot)
        {
            BaseUrl = baseUrl;
            Token = token;
            Host = host;
            PreviousWorkspaceRoot = previousWorkspaceRoot;
        }

        public string BaseUrl { get; }

        public string Token { get; }

        private BrokerTestHostHost Host { get; }

        private string? PreviousWorkspaceRoot { get; }

        public static async Task<BrokerTestHost> StartAsync(string workspaceRoot)
        {
            var root = Path.Combine(Path.GetTempPath(), "ru-broker-conformance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sessionRoot = Path.Combine(root, "session");
            Directory.CreateDirectory(sessionRoot);
            var token = "broker-conformance-" + Guid.NewGuid().ToString("N");
            var descriptorPath = Path.Combine(root, "broker.json");
            var descriptor = new BrokerSessionDescriptor(
                "http://127.0.0.1:" + GetFreePort(),
                token,
                1,
                sessionRoot,
                DateTimeOffset.UtcNow.AddMinutes(30));
            File.WriteAllText(
                descriptorPath,
                JsonSerializer.Serialize(descriptor, BrokerSessionDescriptor.JsonOptions));

            var previousWorkspaceRoot = Environment.GetEnvironmentVariable("RU_WORKSPACE_ROOT");
            Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", workspaceRoot);
            var host = await BrokerTestHostHost.StartAsync(descriptorPath);
            return new BrokerTestHost(descriptor.BaseUrl, token, host, previousWorkspaceRoot);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Environment.SetEnvironmentVariable("RU_WORKSPACE_ROOT", PreviousWorkspaceRoot);
        }

        private static int GetFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
