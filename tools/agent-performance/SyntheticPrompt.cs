using System.Diagnostics.Metrics;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Diagnostics;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;

// The probe never loads user instructions, settings, credentials or recorded history.
sealed class SyntheticPrompt : ISystemPromptContextProvider
{
    public Task<SystemPromptContext> BuildAsync(AgentRun run, ModelContextSnapshot snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var definition = AgentToolDefinitionCatalog.Find("read", "1.0.0")!;
        var capability = new PromptCapability(definition.ToolId, definition.ToolVersion, definition.Description, definition.Scope,
            AgentToolDefinitionCatalog.CanonicalSchemaJson(definition.InputSchema),
            AgentToolDefinitionCatalog.ComputeSchemaDigest(definition.InputSchema), null, "enabled");
        return Task.FromResult(new SystemPromptContext(SystemPromptContext.PromptContextSchemaVersion, "ru-agent", "standard",
            new PromptEnvironment("synthetic-model", "windows", "en-US", "UTC", "2026-10-21", "synthetic"), [], [capability],
            new PromptRuntimeContext([new PromptReference("benchmark_state", "synthetic observations",
                "Retained entries: " + snapshot.Entries.Count)])));
    }
}

sealed class ProbeMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    public ProbeMetrics(Timings stats)
    {
        _listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == HarnessPerformance.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "ru.agent.phase.duration") stats.Add("phase." + tags[0].Value, value);
        });
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "ru.agent.context.cache.lookups") stats.Add("cache." + tags[0].Value, value);
        });
        _listener.Start();
    }
    public void Dispose() => _listener.Dispose();
}
