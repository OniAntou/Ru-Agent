using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ConversationWidthStateTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ru-width", Guid.NewGuid().ToString("N") + ".json");

    [Theory]
    [InlineData(1200, 768)]
    [InlineData(2000, 920)]
    [InlineData(900, 680)]
    [InlineData(700, 640)]
    public void Default_width_follows_the_column_without_exceeding_the_edge_budget(
        double viewport, double expected)
    {
        var state = new ConversationWidthState(new ConversationWidthStore(TempPath()));
        state.SetViewportWidth(viewport);
        Assert.Equal(expected, state.ContentWidth);
    }

    [Fact]
    public void A_dragged_width_is_bounded_and_survives_a_new_state_instance()
    {
        var path = TempPath();
        try
        {
            var first = new ConversationWidthState(new ConversationWidthStore(path));
            first.SetViewportWidth(1200);
            first.PreviewWidth(900);
            Assert.Equal(900, first.ContentWidth);
            Assert.Equal(900 + ConversationWidthState.HandleOutset * 2, first.HandleLayerWidth);
            Assert.Equal(900 + ConversationWidthState.ComposerGutter, first.ComposerWidth);
            first.Commit();

            var second = new ConversationWidthState(new ConversationWidthStore(path));
            second.SetViewportWidth(1200);
            Assert.Equal(900, second.ContentWidth);

            second.SetViewportWidth(700);
            Assert.Equal(640, second.ContentWidth);
            second.PreviewWidth(2000);
            Assert.Equal(640, second.ContentWidth);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Cancel_restores_the_saved_width()
    {
        var state = new ConversationWidthState(new ConversationWidthStore(TempPath()));
        state.SetViewportWidth(1200);
        var saved = state.ContentWidth;
        state.PreviewWidth(900);
        state.CancelDrag();
        Assert.Equal(saved, state.ContentWidth);
    }
}
