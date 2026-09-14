using OpenTelemetry;
using OpenTelemetry.Trace;
using RuAgent.Broker.Diagnostics;
using RuAgent.Harness.Core.Diagnostics;

namespace RuBackend.Diagnostics;

/// <summary>
/// OpenTelemetry provider for the public host process. Covers the backend and
/// harness sources; the broker exports from its own process. OTLP export is
/// added only when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, otherwise traces
/// stay in-process. Never throws: telemetry must not break host startup.
/// </summary>
public static class BackendTelemetry
{
    public static TracerProvider? CreateProvider()
    {
        try
        {
            var builder = Sdk.CreateTracerProviderBuilder()
                .AddSource(HarnessTelemetry.SourceName);
            if (!string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(BrokerTelemetry.OtlpEndpointVariable)))
            {
                builder.AddOtlpExporter();
            }

            return builder.Build();
        }
        catch
        {
            return null;
        }
    }
}
