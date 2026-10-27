using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands : IDisposable
{
    private readonly PublicHostSession _session;
    private readonly HttpClient _httpClient;
    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly TextReader _in;
    private readonly SessionApiClient _sessionClient;
    private readonly AgentApiClient _agentClient;
    private readonly AgentSseClient _sseClient;
    private readonly SettingsApiClient _settingsClient;
    private readonly SkillsApiClient _skillsClient;
    private readonly AgentSlashCommands _slashCommands;
    private readonly RunStarter _runStarter;
    private readonly RunStartCore _runStartCore;
    private Rpc.RpcBridgeServer? _rpcServer;

    public AgentCommands(
        PublicHostSession session,
        HttpClient httpClient,
        TextWriter outWriter,
        TextWriter errWriter,
        TextReader? inReader = null,
        HttpClient? sseHttpClient = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _out = outWriter ?? throw new ArgumentNullException(nameof(outWriter));
        _err = errWriter ?? throw new ArgumentNullException(nameof(errWriter));
        _in = inReader ?? Console.In;
        _sessionClient = new SessionApiClient(session, httpClient, clientSource: "cli");
        _agentClient = new AgentApiClient(session, httpClient);
        _settingsClient = new SettingsApiClient(session, httpClient);
        _skillsClient = new SkillsApiClient(session, httpClient);
        _runStarter = new RunStarter(_sessionClient, _agentClient, _out, _err, _settingsClient);
        _runStartCore = _runStarter.Core;
        _sseClient = new AgentSseClient(session, sseHttpClient ?? httpClient);
        _slashCommands = new AgentSlashCommands(_settingsClient, _sessionClient, outWriter, errWriter, _skillsClient);
    }

    public void Dispose()
    {
        // Injected clients stay owned by the caller (ownsHttpClient is false), so
        // this only releases the wrappers the CLI created around them.
        _agentClient.Dispose();
        _sseClient.Dispose();
    }

    public static async Task<AgentCommands> CreateWithStarterAsync(
        string projectRoot,
        PublicHostStarter? starter,
        HttpClient? httpClient,
        TextWriter outWriter,
        TextWriter errWriter,
        CancellationToken ct)
    {
        starter ??= new PublicHostStarter();
        httpClient ??= new HttpClient();
        var session = await starter.GetOrStartAsync(projectRoot, ct).ConfigureAwait(false);
        return new AgentCommands(session, httpClient, outWriter, errWriter);
    }

    /// <summary>
    /// Streams run events, transparently resuming from the last durable
    /// sequence when the transport breaks mid-stream (bounded retries). A
    /// clean stream end only finishes the turn when the run itself is
    /// terminal; otherwise the server closed early and we resume like a
    /// break. Durable frames never duplicate (cursor-based); transient frames
    /// published during the outage may leave a small gap in live text.
    /// </summary>
    private async Task StreamRunEventsResumedAsync(
        string runId,
        Func<SseFrame, CancellationToken, Task> onFrameAsync,
        CancellationToken ct,
        long afterSequence = 0,
        Func<SseFrame, bool>? stopWhen = null)
    {
        await AgentRunFeed.FollowAsync(
                afterSequence,
                (cursor, token) => _sseClient.ReadEventsAsync(runId, cursor, token),
                async token =>
                {
                    var run = await _agentClient.GetRunAsync(runId, token).ConfigureAwait(false);
                    return run.TerminalAt is not null
                        || string.Equals(run.Status, "completed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(run.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(run.Status, "interrupted", StringComparison.OrdinalIgnoreCase);
                },
                onFrameAsync,
                ct,
                new AgentRunFeedOptions
                {
                    ResetResumeBudgetOnFrame = true,
                    StopWhen = stopWhen,
                    TransportFailureFactory = (failure, resumes) => new AgentApiException(
                        "stream_interrupted",
                        $"event stream broke {resumes} times; last error: {failure.Message}",
                        inner: failure),
                })
            .ConfigureAwait(false);
    }

    public async Task<int> RpcAsync(RpcCommand cmd, CancellationToken cancellationToken)
    {
        var runBridge = new Rpc.RpcRunBridge(
            _sseClient,
            _agentClient,
            publish: notification => _rpcServer is not null && _rpcServer.TryPublish(notification),
            log: message => _err.WriteLine(message));
        var dispatcher = new Rpc.RpcDispatcher(
            _session,
            _sessionClient,
            _agentClient,
            _settingsClient,
            _runStarter,
            runBridge,
            log: _err,
            httpClient: _httpClient);
        var server = new Rpc.RpcBridgeServer(_in, _out, _err, dispatcher, runBridge);
        _rpcServer = server;
        try
        {
            return await server.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runBridge.Dispose();
            _rpcServer = null;
        }
    }
}
