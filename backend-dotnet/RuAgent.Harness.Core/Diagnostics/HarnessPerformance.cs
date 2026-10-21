using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace RuAgent.Harness.Core.Diagnostics;

public enum HarnessPhase
{
    ContextRead, PromptBuild, RequestPrepare, AdmissionCommit, ProviderGate,
    ProviderStream, FirstToken, FirstText, FirstReasoning, FirstTool, ToolExecution, FramePublish,
}

/// <summary>Local timing seam. Fixed phase names only; no user/provider content or identifiers.</summary>
public static class HarnessPerformance
{
    public const string MeterName = "RuAgent.Performance";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("ru.agent.phase.duration", "ms");
    private static readonly Counter<long> ContextCache = Meter.CreateCounter<long>("ru.agent.context.cache.lookups");
    private static readonly string[] Names = Enum.GetNames<HarnessPhase>();

    public static Measurement Measure(HarnessPhase phase) => new(phase, Duration.Enabled ? Stopwatch.GetTimestamp() : 0);

    public static void RecordContextCache(bool hit)
    {
        try { ContextCache.Add(1, new KeyValuePair<string, object?>("result", hit ? "hit" : "miss")); }
        catch { }
    }

    public static void RecordElapsed(HarnessPhase phase, long started)
    {
        if (started == 0 || !Duration.Enabled || !Enum.IsDefined(phase)) return;
        // Meter listeners are optional diagnostics and cannot break a model/tool run.
        try { Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new KeyValuePair<string, object?>("phase", Names[(int)phase])); }
        catch { }
    }

    public readonly struct Measurement(HarnessPhase phase, long started) : IDisposable
    {
        public void Dispose() => RecordElapsed(phase, started);
    }
}
