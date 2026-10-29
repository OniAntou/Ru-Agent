using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Diagnostics;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuBackend.Tests.ModelRunner;

public sealed partial class InProcessModelStepRunnerTests
{
    [Fact]
    public async Task Retry_cooldown_is_measured_separately_from_provider_streaming()
    {
        var owned = new AsyncLocal<bool> { Value = true };
        var phases = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == HarnessPerformance.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            if (owned.Value && tags.Length == 1 && tags[0].Value is string phase) phases.Add(phase);
        });
        listener.Start();
        var provider = new ScriptedProvider((_, attempt) => attempt == 1
            ? Throws(new ModelProviderException("rate_limited", retryable: true))
            : Deltas(new ModelDelta("done", [], completed: true, finishReason: "stop")));
        var runner = Runner(provider);
        runner.StepRetryBackoffsOverride = Enumerable.Repeat(TimeSpan.FromMilliseconds(1), 4).ToArray();
        await runner.RunStepAsync(Request(), _ => ValueTask.CompletedTask, default);
        Assert.Equal(2, provider.Calls);
        Assert.Contains("ProviderRetryWait", phases);
    }
}
