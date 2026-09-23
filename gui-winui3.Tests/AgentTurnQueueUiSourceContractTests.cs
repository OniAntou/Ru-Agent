using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The queue's surfacing: what the composer promises, and where the turns go when a
/// run is in hand. The queue itself is covered by <see cref="AgentTurnQueueTests"/>.
/// </summary>
public sealed class AgentTurnQueueUiSourceContractTests
{
    [Fact]
    public void The_queue_strip_sits_inside_the_composer()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var panel = GuiSource.Require(document, "QueuedTurnsPanel");
        var list = GuiSource.Require(document, "QueuedTurnsList");

        Assert.True(GuiSource.IsDescendantOf(panel, GuiSource.Require(document, "InputBorder")));
        Assert.Equal("0", GuiSource.AttributeValue(panel, "Row"));
        Assert.Equal("ItemsControl", list.Name.LocalName);
    }

    [Fact]
    public void Enter_queues_while_a_run_is_in_hand()
    {
        string code = GuiSource.ViewText("ChatView");

        Assert.Contains(
            "QueueCurrentComposerTurn",
            GuiSource.MethodBody(code, "OnChatInputPreviewKeyDown"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_turn_ends_into_the_queue()
    {
        string code = GuiSource.ViewText("ChatView");

        Assert.Contains(
            "DrainQueuedTurnsAsync",
            GuiSource.MethodBody(code, "CompleteActiveAgentTurnAsync"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Stopping_the_run_holds_the_queue_instead_of_starting_it()
    {
        string code = GuiSource.ViewText("ChatView");

        Assert.Contains(
            "HoldAfterStop",
            GuiSource.MethodBody(code, "StopActiveAgentAsync"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_session_drops_the_queue()
    {
        string code = GuiSource.ViewText("ChatView");

        Assert.Contains(
            "_queuedTurns.Clear",
            GuiSource.MethodBody(code, "ResetAgentState"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_handler_the_queue_markup_wires_exists_in_the_code_behind()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        string code = GuiSource.ViewText("ChatView");

        foreach (var handler in new[] { "OnRemoveQueuedTurnClick", "OnSendQueuedTurnsClick" })
        {
            Assert.Contains(handler, GuiSource.ClickHandlers(document));
            Assert.True(GuiSource.HasMember(code, handler), $"{handler} is wired in markup but not declared.");
        }
    }

    [Fact]
    public void The_composer_says_which_of_the_two_things_enter_will_do()
    {
        string update = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "UpdateSendButtonMode");

        Assert.Contains("AgentComposerPresentation.QueuePlaceholder", update, StringComparison.Ordinal);
        Assert.Contains("AgentComposerPresentation.MessagePlaceholder", update, StringComparison.Ordinal);
    }

    [Fact]
    public void The_strip_and_its_controls_are_named_for_a_screen_reader()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");

        Assert.Equal(
            "Queued messages",
            GuiSource.AttributeValue(GuiSource.Require(document, "QueuedTurnsPanel"), "Name"));
        Assert.Equal(
            "Send queued messages",
            GuiSource.AttributeValue(GuiSource.Require(document, "QueuedTurnsSendButton"), "Name"));
    }
}
