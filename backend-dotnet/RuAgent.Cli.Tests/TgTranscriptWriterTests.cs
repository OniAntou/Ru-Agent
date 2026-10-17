using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;
using TextStyle = Terminal.Gui.Drawing.TextStyle;

namespace RuAgent.Cli.Tests;

/// <summary>
/// Direct coverage for the classic-renderer-to-TG bridge: palette stripping, same-line
/// chunking, whole-line commits, the collapsed <c>ctrl+o</c> block and the pre-Run fallback.
/// </summary>
public sealed class TgTranscriptWriterTests
{
    [Fact]
    public async Task LostHeadRecoveryReplacesTurnProseAndPreservesToolsAndEarlierTurns()
    {
        using var colours = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var renderer = AgentCommands.CreateSessionRenderer(writer);
        Task<bool>? repair = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            writer.WriteLine("Earlier answer");
            renderer.BeginTurn();
            renderer.Transcript.AppendAssistant("incomplete narration");
            renderer.Flush();
            writer.WriteLine("✓ Read sample.txt");
            renderer.Transcript.AppendAssistant("tail of answer");
            renderer.Flush();
            repair = renderer.Transcript.RecoverAssistantTurnAsync("Complete answer.\n```powershell\nGet-Content sample.txt\n```\nAll done.");
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            Assert.Contains(window.Transcript.Lines, row => row.Text == "Earlier answer");
            Assert.Single(window.Transcript.Lines, row => row.Text == "✓ Read sample.txt");
            Assert.Contains(window.Transcript.Lines, row => row.Text == "Complete answer.");
            Assert.Contains(window.Transcript.Lines, row => row.Text.Contains("Get-Content sample.txt"));
            Assert.DoesNotContain(window.Transcript.Lines, row => row.Text.Contains("incomplete narration") || row.Text == "tail of answer");
            Assert.DoesNotContain(window.Transcript.Lines, row => row.Text.Contains('\u001b'));
            app.RequestStop();
            return false;
        });
        app.Run(window);
        Assert.NotNull(repair);
        Assert.True(await repair);
    }

    [Fact]
    public void BackgroundRetryRewindsAfterQueuedStepMarkHasReachedTheView()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var finished = false;
        Task? worker = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            worker = Task.Run(() =>
            {
                writer.BeginAssistantStep("old");
                writer.WriteLine("SUPERSEDED answer");
                writer.RewindAssistantStep("old");
                writer.BeginAssistantStep("new");
                writer.WriteLine("fresh answer");
                app.Invoke(() => finished = true);
            });
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            if (!finished) return true;
            Assert.True(worker!.IsCompletedSuccessfully);
            Assert.DoesNotContain(window.Transcript.Lines, row => row.Text.Contains("SUPERSEDED"));
            Assert.Contains(window.Transcript.Lines, row => row.Text == "fresh answer");
            app.RequestStop();
            return false;
        });
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { app.RequestStop(); return false; });
        app.Run(window);
        Assert.True(finished);
    }

    [Fact]
    public void ReasoningStaysExpandableWithoutAddingThoughtRowsToTheFeed()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var renderer = AgentCommands.CreateSessionRenderer(writer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.Transcript.Append("question", TranscriptStyle.User);
            for (int i = 0; i < 3; i++) renderer.Transcript.ApplyActivity(new RuAgent.Client.AgentActivityModel(
                "act_reason" + i, RuAgent.Protocol.Harness.AgentActivityKind.Thinking,
                RuAgent.Protocol.Harness.AgentActivityState.Completed,
                null, null, null, null, null, null, null, 100, "succeeded", null, null, null,
                Detail: "reasoning " + i));
            Assert.Single(window.Transcript.Lines);
            Assert.Contains(window.Transcript.CollapsedRows, row => row.Text.Contains("reasoning 2"));
            window.Transcript.ToggleExpanded();
            Assert.True(window.Transcript.IsExpanded);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
    [Fact]
    public void RewindAssistantStep_DropsTheSupersededStepRowsAndKeepsEarlierOnes()
    {
        // A retried attempt or a host-suppressed reply is superseded by the step that
        // replaces it: the rows that step painted leave the screen while everything
        // before the step's mark stays.
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var surface = new TuiSurface(writer) { SuppressRepaints = true };
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { Width = 60 };
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            surface.BeginAssistantStep("step_one");
            transcript.AppendAssistant("kept answer");
            transcript.Flush();
            surface.BeginAssistantStep("step_two");
            transcript.AppendAssistant("SUPERSEDED answer");
            transcript.Flush();
            Assert.Contains(window.Transcript.Lines, row => row.Text.Contains("SUPERSEDED"));

            surface.RewindAssistantStep();

            Assert.DoesNotContain(window.Transcript.Lines, row => row.Text.Contains("SUPERSEDED"));
            Assert.Contains(window.Transcript.Lines, row => row.Text.Contains("kept answer"));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void MarkdownPreviewIsLiveAndCompletedRowsMatchWholeLines()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var surface = new TuiSurface(writer) { SuppressRepaints = true };
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true, Width = 40 };
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            transcript.AppendAssistant("Hello **bold");
            surface.Flush();
            Assert.Contains(window.Transcript.Lines, row => row.Text.Contains("Hello"));
            transcript.AppendAssistant("**.\n```csharp");
            transcript.AppendAssistant("\nvar s = \"" + new string('x', 90));
            transcript.AppendAssistant("TAIL_SENTINEL\";\n```\nAfter.");
            transcript.Flush();
            var rows = window.Transcript.Lines.Select(row => row.Text).ToArray();
            Assert.Contains("Hello bold.", rows);
            Assert.Contains(rows, row => row.StartsWith("  1 │ ") && row.Contains("TAIL_SENTINEL"));
            Assert.Equal("After.", rows[^1]);
            Assert.DoesNotContain(rows, row => row.Contains("```"));
            transcript.AppendAssistant("\n---\n");
            transcript.Flush();
            Assert.All(window.Transcript.Lines.TakeLast(2), row => Assert.True(row.Text.Length <= 40));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void ResetDropsTheProvisionalReplyBeforeRetry()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var renderer = AgentCommands.CreateSessionRenderer(new TgTranscriptWriter(app, window.Transcript));
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            renderer.Transcript.AppendAssistant("superseded reply");
            Assert.Contains(window.Transcript.Lines, row => row.Text.Contains("superseded"));
            renderer.Transcript.ResetAssistant();
            renderer.Transcript.AppendAssistant("fresh reply");
            renderer.Flush();
            Assert.DoesNotContain(window.Transcript.Lines, row => row.Text.Contains("superseded"));
            Assert.Contains(window.Transcript.Lines, row => row.Text == "fresh reply");
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
    [Fact]
    public void ChatFactorySelectsScreenCardsOnlyForTheTerminalGuiBridge()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        Assert.True(AgentCommands.CreateSessionRenderer(new TgTranscriptWriter(app, window.Transcript)).Transcript.FullScreenCards);
        Assert.False(AgentCommands.CreateSessionRenderer(new StringWriter()).Transcript.FullScreenCards);
    }
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
            // Match RecoverMissingAssistantTextAsync: finish the renderer's raw
            // Markdown tail, not just the downstream writer's byte buffer.
            transcript.Flush();

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

    /// <summary>
    /// The metadata feature's core contract: the bridge keeps markdown paint as spans
    /// instead of stripping it, so bold and inline code stay styled in the TG transcript.
    /// </summary>
    [Fact]
    public void MarkdownPaintSurvivesTheBridgeAsSpans()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var pinned = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var surface = new TuiSurface(writer) { SuppressRepaints = true };
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true, Width = 80 };
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            transcript.AppendAssistant("Answer with **emphasis** inline and `code` chip.");
            transcript.Flush();
            // AppendAssistant opens a spacer row before the reply; the reply itself is last.
            var row = window.Transcript.Lines[^1];
            Assert.Equal("Answer with emphasis inline and code chip.", row.Text);
            Assert.DoesNotContain('\u001b', row.Text);
            Assert.NotNull(row.Spans);
            Assert.Contains(row.Spans!, span => span.Text == "emphasis" && span.Attr.Style == TextStyle.Bold);
            Assert.Contains(row.Spans!, span => span.Text == "code" && span.Attr == TgPalette.Attr(TuiToken.Ok));
            app.RequestStop();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>Fenced code keeps its syntax paint and the line-number gutter, escape-free.</summary>
    [Fact]
    public void FencedCodeArrivesWithSyntaxSpans()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var pinned = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
        using var window = TgChatWindow.Build();
        var writer = new TgTranscriptWriter(app, window.Transcript);
        var surface = new TuiSurface(writer) { SuppressRepaints = true };
        var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true, Width = 80 };
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            transcript.AppendAssistant("```csharp\nvar value = 42;\n```\nAfter the fence.");
            transcript.Flush();
            var rows = window.Transcript.Lines.ToArray();
            Assert.DoesNotContain(rows, row => row.Text.Contains("```"));
            var codeRow = Assert.Single(rows, row => row.Text.Contains("var value = 42"));
            Assert.StartsWith("  1 │ ", codeRow.Text);
            Assert.DoesNotContain('\u001b', codeRow.Text);
            Assert.NotEmpty(codeRow.Spans!);
            Assert.Equal("After the fence.", rows[^1].Text);
            app.RequestStop();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    /// <summary>A painted notice keeps its tone instead of flattening to the row style.</summary>
    [Fact]
    public void PaintedNoticeRowCarriesItsSpan()
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
            Assert.NotNull(row.Spans);
            var span = Assert.Single(row.Spans!);
            Assert.Equal("  · Search done", span.Text);
            Assert.Equal(TgPalette.ToColor((0, 205, 0)), span.Attr.Foreground);
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
