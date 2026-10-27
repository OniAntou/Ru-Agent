using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgReasoningMetaTests
{
    [Theory]
    [InlineData("default")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData(null)]
    public void Reasoning_selection_renders_beside_model_without_changing_permission(string? effort)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.SetMeta("standard", "my-model", "openrouter");
        window.Composer.SetReasoningEffort(effort); var checkedRow = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(180), () =>
        {
            var row = TgSnapshot.Capture(app)[window.Composer.Frame.Y + TgComposerView.MetadataRow];
            Assert.Contains("my-model", row); Assert.Contains("reasoning: " + (effort ?? "—"), row);
            Assert.True(row.IndexOf("reasoning:", StringComparison.Ordinal) > row.IndexOf("my-model", StringComparison.Ordinal));
            Assert.Equal("standard", window.Composer.Preset); Assert.True(window.Header.Visible);
            checkedRow = true; app.RequestStop(); return false;
        });
        app.Run(window); Assert.True(checkedRow);
    }

    [Fact]
    public void Effort_updates_and_model_change_drops_stale_value()
    {
        using var composer = new TgComposerView();
        composer.SetMeta("standard", "model-a", "provider"); composer.SetReasoningEffort("high");
        composer.SetMeta("plan", "model-a", "provider"); Assert.Equal("high", composer.ReasoningEffort);
        composer.SetReasoningEffort("low"); Assert.Equal("low", composer.ReasoningEffort);
        composer.SetMeta("plan", "model-b", "provider"); Assert.Empty(composer.ReasoningEffort);
        composer.SetReasoningEffort("default"); composer.SetMeta("plan", "model-b", "other-provider");
        Assert.Empty(composer.ReasoningEffort);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(34)]
    [InlineData(80)]
    public void Narrow_metadata_preserves_input_and_uses_bounded_reasoning_text(int width)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.SetMeta("standard", new string('界', 80), new string('p', 100));
        window.Composer.SetReasoningEffort("high\u001b[2J");
        window.Composer.Input.Text = "draft"; var step = 0; var checkedLayout = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            if (step++ == 0) { app.Driver!.SetScreenSize(width, 24); return true; }
            var screen = TgSnapshot.Capture(app);
            Assert.Contains("draft", screen[window.Composer.Frame.Y + 1]);
            var row = screen[window.Composer.Frame.Y + TgComposerView.MetadataRow];
            Assert.DoesNotContain('\u001b', row);
            if (width >= 34) Assert.Contains("reasoning:", row);
            checkedLayout = true; app.RequestStop(); return false;
        });
        app.Run(window); Assert.True(checkedLayout);
    }
}
