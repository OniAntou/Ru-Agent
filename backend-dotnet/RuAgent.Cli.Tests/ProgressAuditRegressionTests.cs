using System.Runtime.ExceptionServices;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>Behavioral acceptance checks for TONG-KET-CONG-VIEC.md, including delta boundaries.</summary>
public sealed class ProgressAuditRegressionTests
{
    [Theory]
    [InlineData("\r\n  var tên = \"😀\";\r\n\trun();\r", "\n  var tên = \"😀\";\n\trun();\n")]
    [InlineData("a\u001bb\u0007c\u0000d\tend", "abcd\tend")]
    [InlineData("Tiếng Việt e\u0301 中文 😀", "Tiếng Việt e\u0301 中文 😀")]
    public void Terminal_paste_preserves_content_without_submitting(string pasted, string expected)
    {
        RunUi((app, window, loop) =>
        {
            // Terminal.Gui's TextView stores canonical Unicode in NFC form.
            expected = expected.Normalize();
            var submissions = 0;
            loop.SetPromptHandler((_, _, _) => { submissions++; return Task.CompletedTask; });
            TgTerminalPaste.Attach(app);
            window.Composer.Input.Text = "before|after";
            window.Composer.Input.InsertionPoint = 6;

            Assert.True(app.RaisePasteEvent(pasted));

            Assert.Equal("before" + expected + "|after", window.Composer.Input.Text);
            Assert.Equal(6 + expected.Length, window.Composer.Input.InsertionPoint);
            Assert.Equal(0, submissions);
        });
    }

    [Fact]
    public void Terminal_paste_replaces_only_the_selection()
    {
        RunUi((app, window, _) =>
        {
            TgTerminalPaste.Attach(app);
            window.Composer.Input.Text = "keep OLD tail";
            window.Composer.Input.InsertionPoint = 5;
            for (var i = 0; i < 3; i++) app.InjectKey(Key.CursorRight.WithShift);
            Assert.Equal("OLD", window.Composer.Input.SelectedText);

            Assert.True(app.RaisePasteEvent("NEW"));

            Assert.Equal("keep NEW tail", window.Composer.Input.Text);
            Assert.Equal(8, window.Composer.Input.InsertionPoint);
        });
    }

    [Fact]
    public void Terminal_paste_at_the_limit_succeeds_and_overflow_is_atomic_with_feedback()
    {
        RunUi((app, window, _) =>
        {
            TgTerminalPaste.Attach(app);
            window.Composer.Input.Text = new string('x', TgPromptEditor.MaxDraftChars - 1);
            window.Composer.Input.InsertionPoint = window.Composer.Input.Text.Length;
            Assert.True(app.RaisePasteEvent("y"));
            var previous = window.Composer.Input.Text;
            var cursor = window.Composer.Input.InsertionPoint;
            Assert.Equal(TgPromptEditor.MaxDraftChars, previous.Length);

            Assert.True(app.RaisePasteEvent("z"));

            Assert.Equal(previous, window.Composer.Input.Text);
            Assert.Equal(cursor, window.Composer.Input.InsertionPoint);
            Assert.Contains("paste too large", window.Hints.FeedbackMessage);
            Assert.True(window.Hints.FeedbackIsError);
        });
    }

    [Fact]
    public void Terminal_paste_does_not_duplicate_when_the_route_is_attached_twice()
    {
        RunUi((app, window, _) =>
        {
            TgTerminalPaste.Attach(app);
            TgTerminalPaste.Attach(app);
            Assert.True(app.RaisePasteEvent("one"));
            Assert.Equal("one", window.Composer.Input.Text);
        });
    }

    [Fact]
    public void Handled_terminal_paste_does_not_touch_the_composer()
    {
        RunUi((app, window, _) =>
        {
            app.Paste += (_, e) => e.Handled = true;
            TgTerminalPaste.Attach(app);
            window.Composer.Input.Text = "keep";
            Assert.True(app.RaisePasteEvent("ignored"));
            Assert.Equal("keep", window.Composer.Input.Text);
        });
    }

    [Fact]
    public void Paste_in_a_modal_does_not_leak_into_the_saved_composer_draft()
    {
        RunUi((app, window, _) =>
        {
            TgTerminalPaste.Attach(app);
            window.Composer.Input.Text = "private draft";
            window.Composer.Input.InsertionPoint = 7;
            window.OpenCommandPalette();
            app.RaisePasteEvent("modal text");
            window.DismissCommandPalette();
            Assert.Equal("private draft", window.Composer.Input.Text);
            Assert.Equal(7, window.Composer.Input.InsertionPoint);
        });
    }

    [Fact]
    public void Terminal_paste_while_searching_commands_updates_the_palette_query()
    {
        RunUi((app, window, _) =>
        {
            TgTerminalPaste.Attach(app);
            window.Composer.Input.Text = "saved draft";
            window.OpenCommandPalette();
            Assert.True(app.RaisePasteEvent("model"));
            Assert.Equal("model", window.CommandPaletteQuery);
            Assert.Equal("saved draft", window.Composer.Input.Text);
        });
    }

    [Fact]
    public void Palette_paste_is_bounded_to_one_line_and_keeps_the_saved_draft()
    {
        RunUi((app, window, _) =>
        {
            TgTerminalPaste.Attach(app);
            window.Composer.Input.Text = "saved draft";
            window.OpenCommandPalette();
            app.RaisePasteEvent("model\t\u001b\r\n/exit");
            Assert.Equal("model", window.CommandPaletteQuery);
            app.RaisePasteEvent(string.Concat(Enumerable.Repeat("😀", 100)));
            Assert.True(window.CommandPaletteQuery.Length <= TgCommandPalette.MaxQueryCharacters);
            Assert.DoesNotContain('\ufffd', window.CommandPaletteQuery);
            window.DismissCommandPalette();
            Assert.Equal("saved draft", window.Composer.Input.Text);
        });
    }

    [Fact]
    public void Terminal_paste_in_a_secret_panel_does_not_edit_or_submit_the_composer()
    {
        RunUi((app, window, _) =>
        {
            TgTerminalPaste.Attach(app);
            window.Composer.Input.Text = "saved draft";
            string? saved = null;
            window.PanelConfirmed += value => saved = value;
            window.ShowSecret("test provider");
            Assert.True(app.RaisePasteEvent("synthetic-key\r\nignored"));
            Assert.Null(saved);
            app.InjectKey(Key.Enter);
            Assert.Equal("synthetic-key", saved);
            Assert.Equal("saved draft", window.Composer.Input.Text);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unwired_paste_shortcut_keeps_the_draft_and_shows_feedback(bool shift)
    {
        RunUi((app, window, _) =>
        {
            window.Composer.Input.Text = "keep";
            app.InjectKey(shift ? Key.V.WithCtrl.WithShift : Key.V.WithCtrl);
            Assert.Equal("keep", window.Composer.Input.Text);
            Assert.Equal("paste unavailable", window.Hints.FeedbackMessage);
        });
    }

    [Theory]
    [InlineData("Gia 140,600 trong README.md", 0)]
    [InlineData("Gia 140,600 trong README.md", 4)]
    [InlineData("Gia 140,600 trong README.md", 9)]
    public void Completed_prose_gets_data_tone_independently_of_delta_boundaries(string full, int split)
    {
        RunUi((app, window, _) =>
        {
            using var color = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
            var writer = new TgTranscriptWriter(app, window.Transcript);
            var surface = new TuiSurface(writer);
            var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true, Width = 80 };
            if (split > 0)
            {
                transcript.AppendAssistant(full[..split]);
                surface.Flush();
            }
            transcript.AppendAssistant(full[split..]);
            transcript.Flush();

            var row = Assert.Single(window.Transcript.Lines, row => row.Text == full);
            Assert.NotNull(row.Spans);
            Assert.Equal(full, string.Concat(row.Spans.Select(span => span.Text)));
            Assert.Contains(row.Spans, span => span.Text == "140,600" && span.Attr == TgPalette.Attr(TuiToken.Brand));
            Assert.Contains(row.Spans, span => span.Text == "README.md" && span.Attr == TgPalette.Attr(TuiToken.Brand2));
        });
    }

    [Fact]
    public void A_plain_suffix_after_painted_streamed_text_remains_covered_by_spans()
    {
        RunUi((app, window, _) =>
        {
            using var color = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
            var writer = new TgTranscriptWriter(app, window.Transcript);
            writer.Write(TuiPalette.Paint("painted", TuiToken.Ok));
            writer.Flush();
            writer.Write(" plain suffix");
            writer.Flush();

            var row = Assert.Single(window.Transcript.Lines);
            Assert.Equal("painted plain suffix", row.Text);
            Assert.NotNull(row.Spans);
            Assert.Equal(row.Text, string.Concat(row.Spans.Select(span => span.Text)));
        });
    }

    [Fact]
    public void Painted_prefix_keeps_its_color_after_a_plain_suffix_is_drawn()
    {
        RunUi((app, window, _) =>
        {
            using var color = TuiPalette.PinLevel(TuiColorLevel.TrueColor);
            var writer = new TgTranscriptWriter(app, window.Transcript);
            writer.Write(TuiPalette.Paint("painted", TuiToken.Ok));
            writer.Flush();
            writer.Write(" plain suffix");
            writer.Flush();
        }, (app, _, _) =>
        {
            var screen = TgSnapshot.Capture(app);
            var row = Array.FindIndex(screen, line => line.Contains("painted plain suffix"));
            Assert.True(row >= 0, "The rendered suffix disappeared from the driver cells.");
            var column = screen[row].IndexOf("painted", StringComparison.Ordinal);
            Assert.Equal(TgPalette.Attr(TuiToken.Ok), TgSnapshot.CellAttribute(app, row, column));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Data_tone_reaches_the_actual_driver_cells_for_prose_and_tables(bool table)
    {
        RunUi((app, window, _) =>
        {
            var writer = new TgTranscriptWriter(app, window.Transcript);
            var surface = new TuiSurface(writer);
            var transcript = new TuiTranscript(surface, new TuiStatusLine(surface)) { FullScreenCards = true, Width = 80 };
            transcript.AppendAssistant(table
                ? "| File | Value |\n| --- | --- |\n| README.md | 140,600 |\n"
                : "Gia 140,600 trong README.md");
            transcript.Flush();
        }, (app, _, _) =>
        {
            var screen = TgSnapshot.Capture(app);
            var row = Array.FindIndex(screen, line => line.Contains("140,600") && line.Contains("README.md"));
            Assert.True(row >= 0, "The completed text is missing from the actual driver cells.");
            Assert.Equal(TgPalette.Attr(TuiToken.Brand), TgSnapshot.CellAttribute(app, row, screen[row].IndexOf("140,600", StringComparison.Ordinal)));
            Assert.Equal(TgPalette.Attr(TuiToken.Brand2), TgSnapshot.CellAttribute(app, row, screen[row].IndexOf("README.md", StringComparison.Ordinal)));
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(20)]
    public void Hard_wrap_keeps_unicode_and_paint_aligned(int width)
    {
        const string text = "Tiếng Việt😀中文e\u0301README.md140,600";
        var source = new[] { new TgSpan(text[..10], TgPalette.Attr(TuiToken.Ok)), new TgSpan(text[10..], TgPalette.Attr(TuiToken.Brand)) };
        var rows = TgAnsiPaint.HardWrapSpans(text, source, width);
        Assert.Equal(text, string.Concat(rows.Select(row => row.Row)));
        Assert.All(rows, row => Assert.Equal(row.Row, string.Concat(row.Spans.Select(span => span.Text))));
        Assert.All(rows, row => Assert.DoesNotContain('\ufffd', row.Row));
    }

    [Fact]
    public void Recovery_keeps_an_assistant_json_example_even_when_it_describes_tool_protocol_fields()
    {
        const string answer = "{\"tool_calls\":[],\"finish_reason\":\"stop\",\"explanation\":\"Đây là JSON ví dụ bạn yêu cầu.\"}";
        Assert.Equal(new[] { answer }, AssistantTextReconciler.ProsePayloads([new("assistanttext", "utf8text", answer)]));
    }

    private static void RunUi(Action<IApplication, TgChatWindow, TgChatLoop> action,
        Action<IApplication, TgChatWindow, TgChatLoop>? inspect = null)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        Exception? failure = null;
        var ran = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            try
            {
                app.Driver!.SetScreenSize(80, 24);
                window.FocusComposer();
                ran = true;
                action(app, window, loop);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (inspect is null || failure is not null) { loop.Stop(); app.RequestStop(); }
            }
            return false;
        });
        if (inspect is not null)
        {
            app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
            {
                try { inspect(app, window, loop); }
                catch (Exception error) { failure = error; }
                finally { loop.Stop(); app.RequestStop(); }
                return false;
            });
        }
        app.AddTimeout(TimeSpan.FromSeconds(5), () => { app.RequestStop(); return false; });
        app.Run(window);
        Assert.True(ran, "The UI loop did not execute the audit check.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
