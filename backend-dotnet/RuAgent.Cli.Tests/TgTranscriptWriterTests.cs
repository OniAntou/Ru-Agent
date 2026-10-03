using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// Direct coverage for the classic-renderer-to-TG bridge: palette stripping, same-line
/// chunking, whole-line commits, the collapsed <c>ctrl+o</c> block and the pre-Run fallback.
/// </summary>
public sealed class TgTranscriptWriterTests
{
    [Fact]
    public void WriteLine_StripsPaletteEscapes()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            writer.WriteLine("\u001b[32m  · Search done\u001b[0m");
            var row = Assert.Single(window.Transcript.Lines);
            Assert.Equal("  · Search done", row.Text);
            Assert.DoesNotContain('\u001b', row.Text);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Write_SameLineFragments_GrowOneRow()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            writer.Write("\u001b[36mEm \u001b[0m");
            writer.Write("kiểm tra ");
            writer.Write("lock.");
            writer.Flush();
            var row = Assert.Single(window.Transcript.Lines);
            Assert.Equal("Em kiểm tra lock.", row.Text);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void WriteLine_AfterAStreamedFragment_CommitsTheFragmentFirst()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            writer.Write("streamed head");
            writer.WriteLine("  · notice");
            Assert.Equal(2, window.Transcript.Lines.Count);
            Assert.Equal("streamed head", window.Transcript.Lines[0].Text);
            Assert.Equal("  · notice", window.Transcript.Lines[1].Text);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>
    /// I2: a bridged summary line that advertises expansion becomes a collapsed block whose
    /// hidden rows Ctrl+O can reveal; the retained rows are stripped on the way in.
    /// </summary>
    [Fact]
    public void CollapsedAffordance_BridgesRetainedRows_AndCtrlOExpands()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript)
        {
            RetainedLinesProvider = () =>
            [
                "\u001b[32m    │ + added line\u001b[0m",
                "\u001b[31m    │ - removed line\u001b[0m",
            ],
        };

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            writer.WriteLine("\u001b[2m    │ · 2 more lines · ctrl+o\u001b[0m");
            Assert.False(window.Transcript.IsExpanded);
            Assert.Equal("    │ · 2 more lines · ctrl+o", Assert.Single(window.Transcript.Lines).Text);
            var hidden = window.Transcript.CollapsedRows;
            Assert.Contains(hidden, row => row.Text == "    │ + added line");
            Assert.Contains(hidden, row => row.Text == "    │ - removed line");
            Assert.DoesNotContain(hidden, row => row.Text.Contains('\u001b', StringComparison.Ordinal));
            window.Transcript.ToggleExpanded();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("+ added line", text);
            Assert.Contains("- removed line", text);
            Assert.Contains("end of expanded block", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void CollapsedAffordance_WithoutRetainedRows_StaysAPlainRow()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            writer.WriteLine("    │ · 2 more lines · ctrl+o");
            Assert.Equal("    │ · 2 more lines · ctrl+o", Assert.Single(window.Transcript.Lines).Text);
            Assert.Empty(window.Transcript.CollapsedRows);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void PreRun_InvokeFallback_DoesNotThrow()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);

        // Before Run there is no loop to marshal onto; the bridge must fall back instead
        // of surfacing NotInitializedException to the renderer.
        writer.WriteLine("before run");
        writer.Write("streamed");
        writer.Flush();
    }

    /// <summary>
    /// I5: the lost-suffix recovery appends through the same surface/writer the turn
    /// streamed through, so the repaired text reaches the TG transcript.
    /// </summary>
    [Fact]
    public void RecoverySuffix_AppendsThroughTheWriter_ToTheTranscript()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var surface = new TuiSurface(writer);
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { Width = 80 };

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            transcript.BeginTurn();
            transcript.AppendAssistant("Em kiểm");
            surface.Flush();

            var streamed = transcript.StreamedAssistantText;
            const string full = "Em kiểm tra lock.";
            var decision = AssistantTextReconciler.Decide(streamed, full);
            Assert.Equal(AssistantTextRecovery.AppendSuffix, decision);
            transcript.AppendAssistant(AssistantTextReconciler.SuffixFor(decision, streamed, full));
            surface.Flush();

            Assert.Contains(
                window.Transcript.Lines,
                row => row.Text.Contains("Em kiểm tra lock.", StringComparison.Ordinal));
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
