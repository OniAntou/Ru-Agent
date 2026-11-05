using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class RemovedTuiCommandsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Removed_commands_never_admit_attachments_or_dispatch_even_while_queue_paused(bool paused)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build(); var loop = new TgChatLoop(window);
        using var pending = new ChatSubmissionState();
        pending.Add(new RuAgent.Cli.Attachments.CliTextAttachment("draft.md", "keep draft", 10));
        loop.SetAttachmentCountProvider(() => pending.Count);
        loop.SetSubmissionOwnershipHandlers(pending.Admit, _ => { });
        var snapshots = 0; var dispatches = 0; var checkedCommands = false;
        loop.SetSubmissionSnapshotFactory(line => { snapshots++; return pending.CreateSnapshot(line, "s", "w", "standard", "b", "m", false); });
        loop.SetPromptHandler((_, _, _) => { dispatches++; return Task.CompletedTask; });
        if (paused) loop.PauseQueuedSubmissions();
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            foreach (var command in new[] { "/delete", "/resume", "/retry", "/release", "/clear", "/follow", "/FOLLOW", "/follow run_example", "/changes", "/CHANGES run_example", "/history", "/HISTORY 2 edit", "/history cancel", "/rewind", "/rewind 1 --yes" })
            {
                window.Composer.Input.Text = command; loop.Submit();
                Assert.Equal(command, window.Composer.Input.Text); Assert.True(window.Hints.FeedbackIsError);
                Assert.Empty(ChatCommandCatalog.FindMatches(command.Split(' ', 2)[0]));
                Assert.DoesNotContain(command, string.Join('\n', TuiSurfaces.Help(120)));
            }
            Assert.Equal(1, pending.Count); Assert.Equal(0, snapshots); Assert.Equal(0, dispatches); Assert.Equal(0, window.Hints.QueuedCount);
            checkedCommands = true; loop.Stop(); app.RequestStop(); return false;
        });
        app.Run(window); Assert.True(checkedCommands);
    }
}
