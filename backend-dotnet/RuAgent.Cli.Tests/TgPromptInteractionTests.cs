using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgPromptInteractionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Newline_key_edits_the_draft_without_sending_it(bool shiftEnter)
    {
        var submitted = new List<string>();
        Render((app, window, loop) =>
        {
            loop.SetPromptHandler((line, _, _) => { submitted.Add(line); return Task.CompletedTask; });
            window.Composer.Input.Text = "draft";
            window.Composer.Input.InsertionPoint = 5;
            app.InjectKey(shiftEnter ? Key.Enter.WithShift : Key.J.WithCtrl);
        }, (_, window, _) =>
        {
            Assert.Empty(submitted);
            Assert.Equal("draft\n", window.Composer.Input.Text);
        });
    }

    [Fact]
    public void Up_inside_a_multiline_draft_moves_the_cursor_before_navigating_history()
    {
        Render((app, window, loop) =>
        {
            loop.RememberForTests("older prompt");
            window.Composer.Input.Text = "first\nsecond";
            window.Composer.Input.InsertionPoint = 10;
            app.InjectKey(Key.CursorUp);
        }, (_, window, _) => Assert.Equal("first\nsecond", window.Composer.Input.Text));
    }

    [Fact]
    public void Multiline_draft_is_visible_in_the_actual_composer_frame()
    {
        Render((_, window, _) => window.Composer.Input.Text = "first line\nsecond line\nthird line",
            (app, window, _) =>
            {
                var rows = TgSnapshot.Capture(app).Skip(window.Composer.Frame.Y).Take(window.Composer.Frame.Height);
                var frame = string.Join('\n', rows);
                Assert.Contains("first line", frame);
                Assert.Contains("second line", frame);
                Assert.Contains("third line", frame);
                Assert.True(window.Transcript.Frame.Bottom < window.Composer.Frame.Y);
                var screen = TgSnapshot.Capture(app);
                Assert.Contains("────", screen[window.HeaderDivider.Frame.Y]);
                Assert.DoesNotContain("┌", screen[window.Composer.Frame.Y]);
                Assert.DoesNotContain(" ru ", screen[window.Composer.Frame.Y]);
                Assert.DoesNotContain("ctrl+j", screen[window.Hints.Frame.Y]);
            });
    }

    [Fact]
    public void Paste_preserves_code_and_inserts_at_the_unicode_cursor()
    {
        Render((_, window, _) =>
        {
            window.Composer.Input.Text = "anh 😀 em";
            window.Composer.Input.InsertionPoint = 6;
            Assert.Equal(6, window.Composer.Input.InsertionPoint);
            Assert.True(window.Composer.Input.TryInsertDraft("\r\n  if (ready)\r\n    run();\r\n"));
        }, (_, window, _) =>
        {
            Assert.Equal("anh 😀\n  if (ready)\n    run();\n em", window.Composer.Input.Text);
            Assert.Equal("anh 😀\n  if (ready)\n    run();\n".Length, window.Composer.Input.InsertionPoint);
        });
    }

    [Fact]
    public void Oversized_paste_preserves_the_entire_draft_and_cursor()
    {
        Render((_, window, _) =>
        {
            window.Composer.Input.Text = "keep draft";
            window.Composer.Input.InsertionPoint = 4;
            Assert.False(window.Composer.Input.TryInsertDraft(new string('x', TgPromptEditor.MaxDraftChars)));
        }, (_, window, _) =>
        {
            Assert.Equal("keep draft", window.Composer.Input.Text);
            Assert.Equal(4, window.Composer.Input.InsertionPoint);
        });
    }

    [Fact]
    public void Backslash_enter_adds_a_line_for_legacy_terminals()
    {
        Render((app, window, loop) =>
        {
            loop.SetPromptHandler((_, _, _) => throw new InvalidOperationException("must not submit"));
            window.Composer.Input.Text = "first\\";
            window.Composer.Input.InsertionPoint = 6;
            app.InjectKey(Key.Enter);
        }, (_, window, _) => Assert.Equal("first\n", window.Composer.Input.Text));
    }

    [Fact]
    public void Word_wrapping_grows_the_editor_to_show_every_visual_row()
    {
        Render((_, window, _) => window.Composer.Input.Text =
            new string('a', 40) + " " + new string('b', 40) + " " + new string('c', 40),
            (_, window, _) => Assert.Equal(window.Composer.Input.Lines, window.Composer.InputHeight));
    }

    [Fact]
    public void Wrapped_cursor_paste_keeps_the_source_offset_and_combining_characters()
    {
        var text = string.Join(' ', Enumerable.Repeat("Tiếng Việt a\u0301 😀", 12));
        var normalized = text.Normalize(System.Text.NormalizationForm.FormC);
        var offset = normalized.IndexOf("Tiếng", 80, StringComparison.Ordinal);
        Render((_, window, _) =>
        {
            window.Composer.Input.Text = text;
            Assert.Equal(normalized, window.Composer.Input.Text);
            window.Composer.Input.InsertionPoint = offset;
            Assert.Equal(offset, window.Composer.Input.InsertionPoint);
            Assert.True(window.Composer.Input.TryInsertDraft("HERE\n"));
        }, (_, window, _) => Assert.Equal(normalized.Insert(offset, "HERE\n"), window.Composer.Input.Text));
    }

    [Fact]
    public void Oversized_submission_reports_feedback_without_a_running_spinner()
    {
        Render((_, window, loop) =>
        {
            window.Composer.Input.Text = new string('x', TgPromptEditor.MaxDraftChars + 1);
            loop.Submit();
        }, (_, window, _) =>
        {
            Assert.Equal(TgHintsState.Idle, window.Hints.ActiveState);
            Assert.False(window.Hints.IsRunning);
            Assert.True(window.Hints.FeedbackIsError);
            Assert.Contains("submission too large", window.Hints.FeedbackMessage);
            Assert.Equal(TgPromptEditor.MaxDraftChars + 1, window.Composer.Input.Text.Length);
        });
    }

    [Fact]
    public void Paused_queue_keeps_its_count_after_the_active_turn_is_interrupted()
    {
        Render((_, window, loop) =>
        {
            loop.SetPromptHandler((_, _, ct) => Task.Delay(System.Threading.Timeout.Infinite, ct));
            window.Composer.Input.Text = "active";
            loop.Submit();
            window.Composer.Input.Text = "queued";
            loop.Submit();
            loop.PauseQueuedSubmissions();
            loop.Cancel();
        }, (_, window, _) =>
        {
            Assert.Equal(TgHintsState.Paused, window.Hints.ActiveState);
            Assert.Equal(1, window.Hints.QueuedCount);
            Assert.False(window.Hints.IsRunning);
        });
    }

    [Fact]
    public void Copy_shortcut_uses_the_injected_writer_without_touching_the_system_clipboard()
    {
        string? copied = null;
        Render((app, window, loop) =>
        {
            window.Transcript.Append("latest answer", TranscriptStyle.Prose);
            loop.SetCopyHandler(text => { copied = text; return Task.FromResult(true); });
            app.InjectKey(Key.C.WithCtrl.WithShift);
        }, (_, window, _) =>
        {
            Assert.Contains("latest answer", copied);
            Assert.Equal("copied to clipboard", window.Hints.FeedbackMessage);
            Assert.Equal(TgHintsState.Idle, window.Hints.ActiveState);
        });
    }

    [Fact]
    public void Command_palette_restores_the_draft_cursor_and_accepts_with_cursor_after_the_command()
    {
        Render((_, window, _) =>
        {
            window.Composer.Input.Text = "keep\nthis draft";
            window.Composer.Input.InsertionPoint = 7;
            window.OpenCommandPalette();
            window.DismissCommandPalette();
            Assert.Equal("keep\nthis draft", window.Composer.Input.Text);
            Assert.Equal(7, window.Composer.Input.InsertionPoint);
            window.OpenCommandPalette();
            window.SetCommandPaletteQuery("/model");
            window.AcceptCommandPaletteSelection();
            Assert.True(window.Composer.Input.TryInsertDraft("my-model"));
        }, (_, window, _) => Assert.Equal("/model my-model", window.Composer.Input.Text));
    }

    private static void Render(Action<IApplication, TgChatWindow, TgChatLoop> act,
        Action<IApplication, TgChatWindow, TgChatLoop> inspect)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        window.Transcript.Append("Existing conversation", TranscriptStyle.Prose);
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            app.Driver!.SetScreenSize(80, 24);
            window.FocusComposer();
            act(app, window, loop);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            try { inspect(app, window, loop); }
            finally { loop.Stop(); app.RequestStop(); }
            return false;
        });
        app.Run(window);
    }
}
