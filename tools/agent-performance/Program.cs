using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol.Harness;
using RuAgent.Runtime.Adapters.Bootstrap;
using RuAgent.Runtime.Adapters.Models;
using RuAgent.Runtime.Model;
using RuBackend.Business.Settings;
using RuBackend.Harness.ModelRunner;
using RuBackend.Harness.Prompting;
using RuBackend.Providers;
using RuBackend.Harness.Workspace;
using RuBackend.Harness.Broker;
using RuAgent.Harness.Core.Policy;
using EventId = RuAgent.Harness.Core.EventId;

if (args.Any(arg => arg != "--no-cache" && !arg.StartsWith("--steps=", StringComparison.Ordinal)
    && !arg.StartsWith("--restart-at=", StringComparison.Ordinal)))
    throw new ArgumentException("Use --no-cache, --steps=1..60 and/or --restart-at=N.");
var stepOption = args.SingleOrDefault(arg => arg.StartsWith("--steps=", StringComparison.Ordinal));
int stepCount = stepOption is null ? 60 : int.Parse(stepOption["--steps=".Length..], System.Globalization.CultureInfo.InvariantCulture);
if (stepCount is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(stepCount));
var restartOption = args.SingleOrDefault(arg => arg.StartsWith("--restart-at=", StringComparison.Ordinal));
int restartAt = restartOption is null ? 0 : int.Parse(restartOption["--restart-at=".Length..], System.Globalization.CultureInfo.InvariantCulture);
if (restartAt < 0 || restartAt >= stepCount) throw new ArgumentOutOfRangeException(nameof(restartAt));
var tempParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ru-agent-performance"));
var root = Path.Combine(tempParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var options = new HarnessSqliteOptions(Path.Combine(root, "probe.db"));
    var health = await HarnessSqliteSchema.InitializeAsync(options, default);
    if (!health.IsAvailable) throw new Exception(health.ErrorCode);
    var keys = new WindowsDpapiKeyStore(Path.Combine(root, "synthetic-key.dat"), options.DatabasePath);
    var payloads = new HarnessPayloadStore(options, new HarnessPayloadCryptography(keys), KnownSecretRedactor.Empty);
    var stats = new Timings();
    var workStates = new SessionWorkStateStore(options, new SessionWorkStateCryptography(keys));
    var store = new TimedStore(new SqliteHarnessEventStore(options, payloads, workStates), stats);
    var toolRoot = Path.Combine(root, "workspace"); Directory.CreateDirectory(toolRoot);

    for (int n = 1; n <= stepCount; n++) await File.WriteAllTextAsync(Path.Combine(toolRoot, "probe_" + n + ".txt"), new string('x', 2048));
    var workspace = await new WindowsWorkspaceIdentityResolver().ResolveAsync(toolRoot, default);
    var policy = PolicySnapshotCanonicalizer.Create(1, AgentPermissionPreset.Standard, workspace, [], DateTimeOffset.UtcNow);
    await new SqliteWorkspaceStore(options).RegisterAsync(new(workspace, new WorkspaceRootCiphertext(new byte[12], new byte[] { 1 }, new byte[16])),
        new(new TrustRequestId("trust_" + new string('1', 32)), workspace, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10)), default);
    var run = new AgentRun(new RunId("run_" + new string('1', 32)), new SessionId(new string('1', 32)), workspace.WorkspaceId,
        new(HarnessRunState.Running), 0, 1, 1, 0, 0, AgentPermissionPreset.Standard, 1, policy.Sha256, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var user = new HarnessPayloadIntent(new PayloadId("payload_" + new string('1', 32)), HarnessPayloadKind.UserPrompt,
        HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text, 4096,
        Encoding.UTF8.GetBytes("Read the next synthetic file each step. Return its contents before reading the next."));
    var created = await store.TryCreateRunAsync(new(run, [new(new EventId("evt_" + new string('1', 32)), HarnessEventKind.RunStateChanged, payloadId: user.PayloadId)],
        [user], [], new(new string('c', 64), new string('d', 64), 1)), default);
    if (created.Outcome != CreateRunOutcome.Created) throw new Exception("seed failed");
    await payloads.CaptureBaseContextAsync(run.RunId, [], DateTimeOffset.UtcNow, default);
    using var http = new HttpClient(new InstantProvider(stats));
    InProcessModelStepRunner NewRunner() => new(new ModelStepService(new OpenAiCompatibleModelProvider(http)))
    {
        CreateProviderBootstrapAsync = (_, _) => Task.FromResult(new ProviderBootstrapData("Synthetic", "chat_completions", new Uri("https://synthetic.invalid/v1"),
            "synthetic-model", "synthetic-key", contextWindowTokens: 2_000_000)),
    };
    var runner = new TimedRunner(NewRunner(), stats);
    IModelContextSource NewSource() => new SqliteModelContextSource(options, payloads, workStates, enableEntryCache: !args.Contains("--no-cache"));
    var context = new TimedContext(NewSource(), stats);
    var prompt = new TimedPrompt(new SyntheticPrompt(), stats);
    using var metricListener = new ProbeMetrics(stats);
    var spool = Path.Combine(root, "spool"); Directory.CreateDirectory(spool);
    var session = RuAgent.Broker.Runtime.BrokerSession.CreateForHost(spool, DateTimeOffset.UtcNow.AddMinutes(30));
    using var realToolService = new RuAgent.Broker.Tools.ToolExecutionService(session, toolRoot, terminalSpoolRoot: spool);
    using var runtime = new RuAgent.Broker.Runtime.BrokerExecutionRuntime(session, realToolService, advertisedCapabilities: session.AdvertisedCapabilities);
    await using var factory = new InProcessToolGatewayFactory(runtime);
    IToolGateway gateway = await factory.CreateAsync(toolRoot, policy, run.RunId, run.SessionId.Value, default);
    var coordinator = new AgentRunCoordinator(runner, new TimedGateway(gateway, stats), store, new AgentContextAssembler(context), new CryptographicIdGenerator(),
        new SystemTimeProvider(), new ToolCallScheduler(), new TransientFrameBuffer(65536), contextLimit: new StaticModelContextLimitProvider(2_000_000 - 8192),
        promptContextProvider: prompt);
    for (int i = 1; i <= stepCount; i++)
    {
        if (restartAt > 0 && i == restartAt + 1) { runner.Reset(NewRunner()); context.Reset(NewSource()); }
        stats.Values.Clear(); var timer = Stopwatch.StartNew();
        var result = await coordinator.RunOneStepAsync(run.RunId, default);
        stats.Add("total", timer.Elapsed.TotalMilliseconds);
        Console.WriteLine(JsonSerializer.Serialize(new { step = i, state = result.Run.State.ToString(), error = result.ErrorCode, ms = stats.Values }));
        if (result.ErrorCode is not null || result.Run.State != HarnessRunState.Running)
            throw new InvalidOperationException("benchmark_step_failed");
    }

}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(root)), tempParent, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("benchmark_cleanup_path_invalid");
    Directory.Delete(root, recursive: true);
}
