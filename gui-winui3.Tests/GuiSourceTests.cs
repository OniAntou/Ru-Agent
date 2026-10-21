using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The source-reading helpers. They exist so assertions about the view can be
/// structural rather than textual, which only holds if they read the right span — a
/// body extractor that returned the whole file would make every "the method contains
/// no literal" assertion pass for the wrong reason.
/// </summary>
public sealed class GuiSourceTests
{
    private const string Sample = """
        public sealed class Sample
        {
            public string First() => "expression";

            private void Second(string name)
            {
                if (name == "guard")
                {
                    return;
                }

                Target.Glyph = "\uE725";
            }

            private void Third() { }
        }
        """;

    /// <summary>The sample with its insignificant whitespace squeezed out.</summary>
    private static string Squeeze(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\s+", string.Empty);

    [Fact]
    public void A_method_body_starts_at_its_signature_and_ends_at_its_brace()
    {
        string body = GuiSource.MethodBody(Sample, "Second");

        Assert.StartsWith("{", body, StringComparison.Ordinal);
        Assert.EndsWith("}", body, StringComparison.Ordinal);
        Assert.Contains("Target.Glyph", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_method_body_excludes_the_methods_around_it()
    {
        // The whole point: a literal elsewhere in the file must not satisfy an
        // assertion about this method.
        string body = GuiSource.MethodBody(Sample, "Second");

        Assert.DoesNotContain("expression", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Third", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_block_does_not_end_the_body_early()
    {
        string body = GuiSource.MethodBody(Sample, "Second");

        Assert.Contains("guard", body, StringComparison.Ordinal);
        Assert.Contains("E725", body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_body_is_returned_rather_than_the_next_method()
    {
        // "{}" or "{ }" — the assertion is about which span came back, not its spacing.
        Assert.Equal("{}", Squeeze(GuiSource.MethodBody(Sample, "Third")));
    }

    [Fact]
    public void The_signature_scan_does_not_match_a_control_statement()
    {
        // `if (` inside another body must not be mistaken for the member.
        string source = """
            private void Wanted()
            {
                if (Flag)
                {
                    Value = "kept";
                }
            }
            """;

        string body = GuiSource.MethodBody(source, "Wanted");

        Assert.Contains("kept", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_helpers_read_the_real_view()
    {
        // A sanity check on the shipped file: the members the contract tests extract
        // must be extractable, so a failure there is about the view and not the helper.
        var code = GuiSource.ViewText("ChatView");

        Assert.Contains("Composer.RestoreDraft", GuiSource.MethodBody(code, "RestoreComposerDraft"), StringComparison.Ordinal);
        Assert.Contains("ChatInputTextBox.Text = text", GuiSource.MethodBody(code, "OnComposerTextReplaced"), StringComparison.Ordinal);
        Assert.Contains("Composer.EditDraft", GuiSource.MethodBody(code, "OnChatInputTyping"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_before_the_declaration_does_not_shadow_it()
    {
        // The bug this pins: a call on its own line reads exactly like a declaration, and
        // the first match was the call — so the method appeared to have no body.
        string source = """
            private void Sender()
            {
                ClearComposerDraft(_activeSessionId);
            }

            private void ClearComposerDraft(string sessionId) => _composerDrafts.Clear(sessionId);
            """;

        string body = GuiSource.MethodBody(source, "ClearComposerDraft");

        Assert.Contains("_composerDrafts.Clear", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_activeSessionId", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_delegating_one_liner_is_read_without_its_neighbours()
    {
        // The exact shape that broke the first attempts: the member's own name also
        // appears as a call on the same line, so the scan must anchor on the
        // declaration and not on the call.
        string source = """
            private void Before() { }

            private void ClearComposerDraft(string sessionId) => _composerDrafts.Clear(sessionId);

            private void After() { }
            """;

        string body = GuiSource.MethodBody(source, "ClearComposerDraft");

        Assert.Contains("_composerDrafts.Clear", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Before", body, StringComparison.Ordinal);
        Assert.DoesNotContain("After", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_member_whose_name_also_appears_as_a_call_is_read_correctly()
    {
        string source = """
            private void RestoreComposerDraft(string sessionId)
            {
                _composerDrafts.Restore(sessionId);
            }
            """;

        string body = GuiSource.MethodBody(source, "RestoreComposerDraft");

        Assert.Contains("_composerDrafts.Restore", body, StringComparison.Ordinal);
        Assert.EndsWith("}", body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_expression_body_is_read_rather_than_rejected()
    {
        // A delegating one-liner is still the member's body, and that is what makes a
        // "the view uses the store" assertion meaningful.
        string body = GuiSource.MethodBody(Sample, "First");

        Assert.Contains("expression", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Second", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_member_is_reported()
    {
        Assert.ThrowsAny<Exception>(() => GuiSource.MethodBody(Sample, "Absent"));
    }

    [Fact]
    public void A_body_does_not_bleed_past_an_unbalanced_close()
    {
        // A stray close brace before the member would otherwise make the scan give up.
        string source = """
            private void Stray() { }

            private void Wanted()
            {
                Value = "kept";
            }
            """;

        Assert.Contains("kept", GuiSource.MethodBody(source, "Wanted"), StringComparison.Ordinal);
    }

    [Fact]
    public void String_literals_are_returned_in_order_and_unescaped_by_nothing()
    {
        var literals = GuiSource.StringLiterals("""A = "one"; B = "two"; C = "three";""");

        Assert.Equal(["one", "two", "three"], literals);
    }

    [Fact]
    public void A_literal_escape_does_not_end_the_literal_early()
    {
        // "\uE725" contains a backslash and would break a naive scanner.
        var literals = GuiSource.StringLiterals("""Glyph = "\uE725"; Name = "Stop";""");

        Assert.Equal([@"\uE725", "Stop"], literals);
    }

    [Fact]
    public void A_snippet_with_no_literals_yields_none()
    {
        Assert.Empty(GuiSource.StringLiterals("var value = 1;"));
    }
}
