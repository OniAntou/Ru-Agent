using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using RuAgent.Broker.Diagnostics;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// The counters exist to answer questions a span list cannot: how often a session
/// re-reads a path, and which bounded code a tool failure carries. A listener is
/// process-wide, so this collection never runs in parallel with another.
/// </summary>
[CollectionDefinition("tool-telemetry", DisableParallelization = true)]
public sealed class ToolTelemetryCollection;

[Collection("tool-telemetry")]
public sealed class ToolTelemetryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ru-telemetry-" + Guid.NewGuid().ToString("N"));

    public ToolTelemetryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_repeated_read_is_counted_as_a_repeat_and_the_first_one_is_not()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "hello");
        using var captured = new MeasurementCapture();

        var session = BrokerSession.CreateForHost(_root, DateTimeOffset.UtcNow.AddMinutes(5));
        session.Open("telemetry", new HashSet<string>([ToolScope.SystemRead, ToolScope.WorkspaceRead]));
        await using var service = new ToolExecutionService(session, _root);

        await service.ExecuteAsync(ReadEnvelope("r1", "a.txt"), CancellationToken.None);
        await service.ExecuteAsync(ReadEnvelope("r2", "a.txt"), CancellationToken.None);

        var pages = captured.For("ru.tool.read_pages");
        Assert.Equal(["false", "true"], pages.Select(tags => tags["repeated"]));
        Assert.All(pages, tags => Assert.Equal("read", tags["tool.id"]));
    }

    [Fact]
    public async Task A_failed_tool_call_is_counted_with_its_bounded_status()
    {
        using var captured = new MeasurementCapture();

        var session = BrokerSession.CreateForHost(_root, DateTimeOffset.UtcNow.AddMinutes(5));
        session.Open("telemetry", new HashSet<string>([ToolScope.SystemRead, ToolScope.WorkspaceRead]));
        await using var service = new ToolExecutionService(session, _root);

        await service.ExecuteAsync(ReadEnvelope("r1", "missing.txt"), CancellationToken.None);

        var call = Assert.Single(captured.For("ru.tool.calls"));
        Assert.Equal("read", call["tool.id"]);
        Assert.Equal(ToolResultStatuses.Failed, call["tool.status"]);
        Assert.False(string.IsNullOrEmpty(call.GetValueOrDefault("error.code")));
    }

    private static AgentEnvelope ReadEnvelope(string id, string path) =>
        new(1, id, AgentMessageTypes.ToolRequest, "telemetry", null, 30000, new JsonObject
        {
            ["tool_id"] = "read",
            ["tool_version"] = "1.0.0",
            ["arguments"] = new JsonObject { ["path"] = path },
        });

    private sealed class MeasurementCapture : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<(string Instrument, Dictionary<string, string?> Tags)> _items = new();
        private readonly MeterListener _listener = new();

        public MeasurementCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == BrokerTelemetry.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                var copy = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var tag in tags)
                {
                    copy[tag.Key] = tag.Value?.ToString();
                }

                lock (_gate)
                {
                    _items.Add((instrument.Name, copy));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<Dictionary<string, string?>> For(string instrument)
        {
            lock (_gate)
            {
                return _items.Where(item => item.Instrument == instrument)
                    .Select(item => item.Tags)
                    .ToArray();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
