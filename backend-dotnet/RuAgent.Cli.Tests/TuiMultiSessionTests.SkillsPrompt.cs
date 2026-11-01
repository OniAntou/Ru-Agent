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
    public async Task Natural_language_skill_request_is_submitted_as_a_normal_model_prompt()
    {
        const string prompt = "Dùng skill review để kiểm tra mã nguồn của dự án.";
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build();
        using var host = new ConcurrentHost();
        using var http = new HttpClient(host);
        using var commands = new AgentCommands(ChatRewindWorkflowTests.Host.Session, http, TextWriter.Null, TextWriter.Null);
        var writer = new TgTranscriptWriter(app, page.Transcript);
        var path = Path.GetTempPath();
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, path, path, string.Empty,
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Exception? failure = null;
        var stage = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgSessionHostWindow) return true;
                if (stage == 0)
                {
                    page.FocusComposer();
                    page.Composer.Input.Text = prompt;
                    app.InjectKey(Key.Enter);
                    stage++;
                }
                else if (host.Streams.ContainsKey("session_1"))
                {
                    Assert.Equal(prompt, host.Prompts["session_1"]);
                    page.Composer.Input.Text = "/exit";
                    app.InjectKey(Key.Enter);
                    stage++;
                    return false;
                }
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
            return !deadline.IsCancellationRequested;
        });
        Assert.Equal(0, await commands.RunPreparedChatAsync(new ChatCommand(path, null, "standard", false), bootstrap, deadline.Token));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(2, stage);
        Assert.Equal(1, host.CreatedSessions);
        Assert.Single(host.Cancels);
    }
}
