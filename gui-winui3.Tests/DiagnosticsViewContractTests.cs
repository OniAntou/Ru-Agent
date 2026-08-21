using System.Text;
using System.Text.Json;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class DiagnosticsViewContractTests
{
    [Fact]
    public void DiagnosticsViewPresentationSourcesContainNoEmoji()
    {
        foreach (var sourceName in new[] { "DiagnosticsView.xaml", "DiagnosticsView.xaml.cs" })
        {
            foreach (var rune in File.ReadAllText(FindViewSource(sourceName)).EnumerateRunes())
            {
                Assert.False(
                    IsEmojiRune(rune),
                    $"{sourceName} contains prohibited emoji U+{rune.Value:X4}.");
            }
        }
    }

    [Fact]
    public void DiagnosticEventDto_deserializes_correctly()
    {
        var json = """
        {
            "event_id": "evt_123",
            "ts": 1724150400.123,
            "iso": "2026-08-20T10:40:00.123Z",
            "level": "info",
            "component": "worker.voice",
            "event_code": "speech.synthesize.start",
            "correlation_id": "c_abc123",
            "duration_ms": 42.5,
            "status": "ok"
        }
        """;

        var dto = JsonSerializer.Deserialize<DiagnosticEventDto>(json);
        Assert.NotNull(dto);
        Assert.Equal("evt_123", dto.EventId);
        Assert.Equal("speech.synthesize.start", dto.EventCode);
        Assert.Equal("c_abc123", dto.CorrelationId);
        Assert.Equal(42.5, dto.DurationMs);
        Assert.Equal("ok", dto.Status);
    }

    [Fact]
    public void Export_forwards_component_and_event_code_filters_and_handles_api_failure()
    {
        var source = File.ReadAllText(FindViewSource("DiagnosticsView.xaml.cs"));

        Assert.Contains("component: component", source, StringComparison.Ordinal);
        Assert.Contains("eventCode: eventCode", source, StringComparison.Ordinal);
        Assert.Contains("if (ndjson == null)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllText", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsView_batches_rows_and_skips_unchanged_renders()
    {
        var source = File.ReadAllText(FindViewSource("DiagnosticsView.xaml.cs"));

        Assert.Contains("BatchObservableCollection<DiagnosticRow>", source, StringComparison.Ordinal);
        Assert.Contains("Rows.ReplaceAll", source, StringComparison.Ordinal);
        Assert.Contains("SequenceEqual(visibleEvents)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Rows.Clear()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsView_tolerates_filter_events_before_list_is_loaded()
    {
        var source = File.ReadAllText(FindViewSource("DiagnosticsView.xaml.cs"));

        Assert.Contains("FindScrollViewer(DependencyObject? root)", source, StringComparison.Ordinal);
        Assert.Contains("if (root is null)", source, StringComparison.Ordinal);
    }

    private static bool IsEmojiRune(Rune rune)
    {
        var value = rune.Value;
        return value is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139
            or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x20E3 or 0xFE0F
            or >= 0x2194 and <= 0x21FF
            or >= 0x2300 and <= 0x23FF
            or >= 0x2600 and <= 0x27BF
            or >= 0x2934 and <= 0x2935
            or >= 0x2B00 and <= 0x2BFF
            or >= 0x1F000 and <= 0x1FAFF
            or >= 0xE0020 and <= 0xE007F;
    }

    private static string FindViewSource(string sourceName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var sourcePath = Path.Combine(
                directory.FullName,
                "gui-winui3",
                "Views",
                sourceName);
            if (File.Exists(sourcePath))
            {
                return sourcePath;
            }
        }

        throw new FileNotFoundException($"Could not locate Views/{sourceName}.");
    }
}
