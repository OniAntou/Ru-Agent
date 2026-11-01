using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class TuiMultiSessionTests
{
    [Fact]
    public async Task Changing_Bs_model_between_As_selection_check_and_create_preserves_As_submitted_model()
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var pageA = TgChatWindow.Build();
        using var host = new ConcurrentHost { DelayFirstPolicy = true }; using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, TextWriter.Null, TextWriter.Null);
        var writer = new TgTranscriptWriter(app, pageA.Transcript); var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, path, path, string.Empty,
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, pageA, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stage = 0; TgChatWindow? pageB = null; Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgSessionHostWindow shell) return true;
                switch (stage)
                {
                    case 0:
                        pageA.FocusComposer(); if (!pageA.Composer.Input.HasFocus) return true;
                        pageA.Composer.Input.Text = "A uses the original model"; app.InjectKey(Key.Enter); stage++; break;
                    case 1 when host.FirstPolicyEntered.Task.IsCompleted:
                        pageA.Composer.Input.Text = "/new"; app.InjectKey(Key.Enter); stage++; break;
                    case 2 when !ReferenceEquals(shell.ActivePage, pageA):
                        pageB = shell.ActivePage!; pageB.FocusComposer();
                        pageB.Composer.Input.Text = "/model gpt-4.1"; app.InjectKey(Key.Enter); stage++; break;
                    case 3 when pageB!.Composer.Model == "gpt-4.1" && !pageB.Hints.IsRunning:
                        host.AllowFirstPolicy.TrySetResult(); stage++; break;
                    case 4 when host.Streams.ContainsKey("session_1"):
                        Assert.Equal("gpt-4o", host.AdmittedModels["session_1"]);
                        Assert.Equal("gpt-4.1", pageB!.Composer.Model);
                        pageB.Composer.Input.Text = "/exit"; app.InjectKey(Key.Enter); stage++; return false;
                }
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
            return true;
        });
        Assert.Equal(0, await commands.RunPreparedChatAsync(new ChatCommand(path, null, "standard", false), bootstrap, deadline.Token));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(5, stage); Assert.Single(host.Cancels);
    }
}
