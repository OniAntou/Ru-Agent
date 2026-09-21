using System.Text;
using System.Text.Json;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Diagnostics surface: no emoji in what it renders, typed log rows, and rows that
/// are swapped in one batch so a streaming log does not re-render per line.
///
/// The batching claim is exercised on the collection that implements it rather than
/// by searching the view for a method name.
/// </summary>
public sealed class DiagnosticsViewContractTests
{
    [Fact]
    public void DiagnosticsMarkupContainsNoEmojiInAnyRenderedText()
    {
        var document = GuiSource.Xml("Views", "DiagnosticsView.xaml");
        var offenders = new List<string>();

        foreach (var element in document.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName is not ("Text" or "Content" or "Header"
                    or "PlaceholderText" or "Description" or "ToolTipService.ToolTip"))
                {
                    continue;
                }

                foreach (var rune in attribute.Value.EnumerateRunes())
                {
                    if (EmojiPolicy.IsEmojiRune(rune))
                    {
                        offenders.Add($"<{element.Name.LocalName} {attribute.Name.LocalName}> U+{rune.Value:X4}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Diagnostics_code_behind_declares_no_emoji_in_string_literals()
    {
        var offenders = new List<string>();
        foreach (var literal in EmojiPolicy.StringLiterals(GuiSource.Text("Views", "DiagnosticsView.xaml.cs")))
        {
            foreach (var rune in literal.EnumerateRunes())
            {
                if (EmojiPolicy.IsEmojiRune(rune))
                {
                    offenders.Add($"U+{rune.Value:X4}");
                }
            }
        }

        Assert.Empty(offenders);
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
    public void DiagnosticEventDto_tolerates_a_row_with_only_the_required_fields()
    {
        // The log is append-only and older rows predate optional fields; a missing
        // duration or correlation id must not make the row unreadable.
        var json = """
        {
            "event_id": "evt_min",
            "ts": 1724150400.0,
            "iso": "2026-08-20T10:40:00.000Z",
            "level": "warn",
            "component": "host.http",
            "event_code": "http.request"
        }
        """;

        var dto = JsonSerializer.Deserialize<DiagnosticEventDto>(json);

        Assert.NotNull(dto);
        Assert.Equal("evt_min", dto.EventId);
        Assert.Equal("warn", dto.Level);
        Assert.Null(dto.DurationMs);
        Assert.Null(dto.CorrelationId);
    }

    [Fact]
    public void Swapping_the_visible_rows_notifies_once_for_the_whole_batch()
    {
        // A streaming log swaps its visible window every refresh; doing that as N
        // Add/Remove notifications would re-render per line.
        var rows = new BatchObservableCollection<RuApp.Views.DiagnosticRow>();
        var notifications = 0;
        rows.CollectionChanged += (_, _) => notifications++;

        rows.ReplaceAll(new[] { Row("a"), Row("b"), Row("c") });
        Assert.Equal(1, notifications);

        var before = rows.ToList();
        rows.ReplaceAll(new[] { Row("a"), Row("b"), Row("c") });
        Assert.Equal(2, notifications);

        // The batch is applied wholesale, not merged: the window is authoritative.
        Assert.Equal(before.Count, rows.Count);
    }

    [Fact]
    public void The_diagnostics_view_never_reads_or_writes_files_itself()
    {
        // The backend owns log storage. The view may hand bytes to a picker, but
        // reaching for the filesystem directly would bypass the host's bounds.
        var view = typeof(RuApp.Views.DiagnosticsView);
        var methods = view.GetMethods(
            System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Public);

        Assert.DoesNotContain(
            methods,
            method => method.DeclaringType == view
                && method.Name.Contains("File", StringComparison.OrdinalIgnoreCase)
                && (method.Name.Contains("Read", StringComparison.OrdinalIgnoreCase)
                    || method.Name.Contains("Write", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Re_initializing_the_diagnostics_view_replaces_its_timer_and_controller()
    {
        // A reconnect calls Initialize again with a freshly built ApiService after the old
        // one was disposed. Building a second timer and controller without tearing down the
        // first left a live 3s poller behind on every reconnect, all of them aimed at the
        // disposed service.
        var body = GuiSource.MethodBody(GuiSource.Text("Views", "DiagnosticsView.xaml.cs"), "Initialize");

        int teardown = body.IndexOf("DisposeRefreshInfrastructure();", StringComparison.Ordinal);
        int newTimer = body.IndexOf("_refreshTimer = new DispatcherTimer", StringComparison.Ordinal);
        int newController = body.IndexOf("_refreshController = new RefreshController", StringComparison.Ordinal);

        Assert.True(teardown >= 0, "Initialize must tear the previous refresh pair down.");
        Assert.True(newTimer >= 0 && newController >= 0, "Initialize must build the refresh pair.");
        Assert.True(teardown < newTimer, "The old timer must go before a new one is created.");
        Assert.True(teardown < newController, "The old controller must go before a new one is created.");
    }

    [Fact]
    public void Disposing_the_diagnostics_view_uses_the_same_teardown_as_re_initializing()
    {
        // Two teardown paths would drift, and the one that drifts is the one that leaks.
        var dispose = GuiSource.MethodBody(GuiSource.Text("Views", "DiagnosticsView.xaml.cs"), "Dispose");

        Assert.Contains("DisposeRefreshInfrastructure();", dispose, StringComparison.Ordinal);
    }

    [Fact]
    public void Re_initializing_the_log_view_takes_the_new_service_instead_of_returning_early()
    {
        // LogView returned as soon as a timer existed, so the reconnect's new ApiService was
        // dropped on the floor and the view kept polling the disposed one every 3 seconds.
        var body = GuiSource.MethodBody(GuiSource.Text("Views", "LogView.xaml.cs"), "Initialize");

        Assert.DoesNotContain("if (_refreshTimer != null)\n            {\n                return;", body.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("_apiService = apiService;", body, StringComparison.Ordinal);
        Assert.Contains("_refreshTimer.Tick -= OnRefreshTimerTick;", body, StringComparison.Ordinal);
    }

    private static RuApp.Views.DiagnosticRow Row(string id) =>
        new(time: "10:40:00", level: "info", component: "host.http", summary: id, detail: string.Empty);
}
