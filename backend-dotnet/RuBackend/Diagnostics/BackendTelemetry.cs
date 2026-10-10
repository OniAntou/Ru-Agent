using OpenTelemetry;
using OpenTelemetry.Trace;
using RuAgent.Broker.Diagnostics;
using RuAgent.Harness.Core.Diagnostics;

namespace RuBackend.Diagnostics;

/// <summary>
/// OpenTelemetry provider for the public host process. Covers the backend,
/// harness and in-process execution sources: tool spans are produced on
/// <see cref="BrokerTelemetry.SourceName"/> by the runtime embedded in this
/// process, so the provider is the only exporter. OTLP export is added only
/// when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, otherwise traces stay
/// in-process. Never throws: telemetry must not break host startup.
/// </summary>
public static class BackendTelemetry
{
    public static TracerProvider? CreateProvider()
    {
        try
        {
            var builder = Sdk.CreateTracerProviderBuilder()
                .AddSource(HarnessTelemetry.SourceName)
                .AddSource(BrokerTelemetry.SourceName);
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
