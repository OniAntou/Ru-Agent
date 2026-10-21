using System.Diagnostics;
using System.Diagnostics.Metrics;
using RuAgent.Harness.Core.Diagnostics;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Diagnostics;

public sealed class HarnessPerformanceTests
{
    [Fact]
    public void Timing_records_only_a_fixed_phase_and_numeric_duration_and_rejects_unknown_phases()
    {
        var owned = new AsyncLocal<bool>();
        var samples = new List<(double Value, string Phase)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == HarnessPerformance.MeterName) owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            if (!owned.Value) return;
            Assert.Equal(1, tags.Length);
            Assert.Equal("phase", tags[0].Key);
            samples.Add((value, (string)tags[0].Value!));
        });
        listener.Start();
        owned.Value = true;
        HarnessPerformance.RecordElapsed((HarnessPhase)int.MaxValue, Stopwatch.GetTimestamp());
        using (HarnessPerformance.Measure(HarnessPhase.ContextRead)) { }
        Assert.Equal("ContextRead", Assert.Single(samples).Phase);
        Assert.True(samples[0].Value >= 0);
    }
}
