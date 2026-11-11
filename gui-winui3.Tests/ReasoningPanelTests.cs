using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ReasoningPanelTests
{
    [Theory]
    [InlineData("default", "Uses the model's own default effort.")]
    [InlineData("LOW", "Thinks briefly before answering.")]
    [InlineData("medium", "A middle setting between speed and thoroughness.")]
    [InlineData("high", "Thinks longer before answering.")]
    [InlineData("xhigh", "The most thinking the model offers.")]
    public void Each_known_effort_has_one_plain_sentence(string effort, string expected) =>
        Assert.Equal(expected, ReasoningEffortDescriptions.For(effort));

    [Fact]
    public void An_unknown_effort_is_named_not_invented() =>
        Assert.Equal("Provider setting: turbo.", ReasoningEffortDescriptions.For("turbo"));

    [Fact]
    public void The_picker_scrolls_to_the_model_in_use_only_when_it_opens_never_on_a_rebuild()
    {
        // A rebuild (catalog update, search) used to call bring-into-view on the selected row,
        // throwing the list to it under the reader. Now only opening centres the row, and a
        // rebuild keeps the reader's offset.
        string picker = GuiSource.Text("Views", "ProviderModelPicker.xaml.cs");
        Assert.Contains("StartCursorOnSelection(scrollToRow: true)", GuiSource.MethodBody(picker, "FocusSearch"), StringComparison.Ordinal);
        Assert.Contains("StartCursorOnSelection(scrollToRow: false)", GuiSource.MethodBody(picker, "RebuildKeyboard"), StringComparison.Ordinal);
        Assert.Contains("savedOffset", GuiSource.MethodBody(picker, "Apply"), StringComparison.Ordinal);
        Assert.Contains("ChangeView", GuiSource.MethodBody(picker, "RestoreScroll"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_reasoning_panel_glides_a_pill_fades_its_sentence_and_the_picker_fades_its_cursor()
    {
        string chat = GuiSource.ViewText("ChatView");
        string cursor = GuiSource.MethodBody(chat, "OnReasoningCursorChanged");
        Assert.Contains("MoveReasoningPill", cursor, StringComparison.Ordinal);
        Assert.Contains("UiMotion.FadeOpacity(_reasoningDescription", cursor, StringComparison.Ordinal);
        Assert.Contains("UiMotion.SlideX", GuiSource.MethodBody(chat, "MoveReasoningPill"), StringComparison.Ordinal);

        string picker = GuiSource.Text("Views", "ProviderModelPicker.xaml.cs");
        // Hover is instant: the marked row is painted directly, with no fade.
        string cursorBody = GuiSource.MethodBody(picker, "OnCursorChanged");
        Assert.Contains(".Background = i == index ? _brushes.Cursor : _brushes.Rest", cursorBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FadeBrush", cursorBody, StringComparison.Ordinal);
        Assert.Contains("UiMotion.FadeBrush", GuiSource.MethodBody(picker, "OnSearchFocusChanged"), StringComparison.Ordinal);
    }
}