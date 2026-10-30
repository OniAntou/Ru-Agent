namespace RuAgent.Broker.Tools;

public sealed record ToolBinding(
    ToolManifest Manifest,
    ToolExecutor Executor,
    bool ModelEnabled,
    string Availability);
