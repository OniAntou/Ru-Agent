using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class TuiMultiSessionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pending_navigation_retains_its_source_tab_but_not_unrelated_idle_tabs(bool closeSource)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var pageA = TgChatWindow.Build();
        using var host = new ConcurrentHost();
        using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, TextWriter.Null, TextWriter.Null);
        var writer = new TgTranscriptWriter(app, pageA.Transcript);
        var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, path, path, string.Empty,
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, pageA, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stage = 0;
        TgChatWindow? pageB = null;
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgSessionHostWindow shell) return true;
                switch (stage)
                {
                    case 0:
                        pageA.FocusComposer();
                        app.InjectKey(Key.N.WithAlt);
                        stage++;
                        break;
                    case 1 when !ReferenceEquals(shell.ActivePage, pageA):
                        pageB = shell.ActivePage!;
                        host.BlockSessionCreation = true;
                        pageB.FocusComposer();
                        pageB.Composer.Input.Text = "/new";
                        app.InjectKey(Key.Enter);
                        stage++;
                        break;
                    case 2 when host.CreatedSessions == 2:
                        if (!closeSource) app.InjectKey(Key.F6);
                        var target = closeSource ? pageB! : pageA;
                        Assert.Same(target, shell.ActivePage);
                        target.FocusComposer();
                        target.Composer.Input.Text = "/sessions close";
                        app.InjectKey(Key.Enter);
                        Assert.Same(pageB, shell.ActivePage);
                        if (closeSource)
                        {
                            Assert.True(pageB!.Hints.FeedbackIsError);
                            app.InjectKey(Key.F6);
                            Assert.Same(pageA, shell.ActivePage);
                            app.InjectKey(Key.F6);
                        }
                        pageB!.Composer.Input.Text = "/exit";
                        app.InjectKey(Key.Enter);
                        stage++;
                        return false;
                }
                return !deadline.IsCancellationRequested;
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
        });
        Assert.Equal(0, await commands.RunPreparedChatAsync(new ChatCommand(path, null, "standard", false), bootstrap, deadline.Token));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(3, stage);
        Assert.Equal(2, host.CreatedSessions);
        Assert.True(host.CreateCancelled);
        Assert.Empty(host.Streams);
        Assert.Empty(host.Cancels);
    }
}
