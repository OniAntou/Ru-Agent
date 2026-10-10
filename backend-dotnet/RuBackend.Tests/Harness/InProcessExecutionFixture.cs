extern alias Broker;

using BrokerExecutionRuntime = Broker::RuAgent.Broker.Runtime.BrokerExecutionRuntime;
using BrokerSession = Broker::RuAgent.Broker.Runtime.BrokerSession;
using BrokerToolExecutionService = Broker::RuAgent.Broker.Tools.ToolExecutionService;

namespace RuBackend.Tests.Harness;

/// <summary>
/// A throwaway in-process execution authority for one test: a descriptor-free
/// session, a real tool service with its own temp spool root, the runtime and
/// the gateway factory. Without a workspace root it creates and owns one; an
/// injected root (the conformance suite's) is left to its owner. Disposal
/// releases the factory, the runtime and the temp directories.
/// </summary>
internal sealed class InProcessExecutionFixture : IDisposable
{
    private readonly string _spoolRoot;
    private readonly bool _ownsWorkspace;

    public InProcessExecutionFixture(string? workspaceRoot = null)
    {
        _ownsWorkspace = workspaceRoot is null;
        WorkspaceRoot = workspaceRoot ?? Path.Combine(
            Path.GetTempPath(),
            "ru-inprocess-ws-" + Guid.NewGuid().ToString("N"));
        if (_ownsWorkspace)
        {
            Directory.CreateDirectory(WorkspaceRoot);
        }

        _spoolRoot = Path.Combine(
            Path.GetTempPath(),
            "ru-inprocess-spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_spoolRoot);
        Session = BrokerSession.CreateForHost(_spoolRoot, DateTimeOffset.UtcNow.AddMinutes(30));
        Tools = new BrokerToolExecutionService(
            Session,
            WorkspaceRoot,
            terminalSpoolRoot: _spoolRoot);
        Runtime = new BrokerExecutionRuntime(
            Session,
            Tools,
            advertisedCapabilities: Session.AdvertisedCapabilities);
        Factory = new global::RuBackend.Harness.Broker.InProcessToolGatewayFactory(Runtime);
    }

    public string WorkspaceRoot { get; }

    public BrokerSession Session { get; }

    public BrokerToolExecutionService Tools { get; }

    public BrokerExecutionRuntime Runtime { get; }

    public global::RuBackend.Harness.Broker.InProcessToolGatewayFactory Factory { get; }

    public void Dispose()
    {
        Factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Runtime.Dispose();
        Tools.Dispose();
        TryDelete(_spoolRoot);
        if (_ownsWorkspace)
        {
            TryDelete(WorkspaceRoot);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
