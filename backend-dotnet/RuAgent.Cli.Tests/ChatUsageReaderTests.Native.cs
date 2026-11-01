using RuAgent.Cli.Images;
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class ChatUsageReaderTests
{
    [Theory]
    [InlineData(-1, "")]
    [InlineData(0, "synthetic-model")]
    [InlineData(1, "System prompt")]
    [InlineData(2, "Cache read")]
    public async Task Native_status_picker_opens_each_report_and_escape_preserves_attachments(int index, string expected)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build(); using var host = new Host(); using var http = new HttpClient(host);
        using var errors = new StringWriter(); using var commands = new AgentCommands(Session, http, TextWriter.Null, errors);
        var writer = new TgTranscriptWriter(app, page.Transcript); var path = Path.GetTempPath();
        using var submissions = new ChatSubmissionState();
        submissions.Add(new CliImageAttachment("synthetic.png", "image/png", "aW1hZ2U=", 1, 1, 5));
        var bootstrap = new AgentCommands.ChatBootstrapContext(submissions, writer, path, path, Owner,
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Exception? failure = null; var stage = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgSessionHostWindow) return true;
                var screen = string.Join('\n', TgSnapshot.Capture(app));
                if (stage == 0)
                { page.FocusComposer(); page.Composer.Input.Text = "/status"; app.InjectKey(Key.Enter); stage++; }
                else if (stage == 1 && page.IsModalPanelOpen && screen.Contains("Session usage"))
                {
                    Assert.Contains("Overview", screen); Assert.Contains("Context", screen);
                    if (index < 0) { app.InjectKey(Key.Esc); stage = 3; }
                    else { for (var move = 0; move < index; move++) app.InjectKey(Key.CursorDown); app.InjectKey(Key.Enter); stage++; }
                }
                else if (stage == 2 && page.IsDocumentPanelOpen)
                {
                    Assert.Contains(expected, screen);
                    Assert.Single(submissions.Images);
                    app.InjectKey(Key.Esc); stage++;
                }
                else if (stage == 3 && !page.IsDocumentPanelOpen && !page.IsModalPanelOpen)
                {
                    Assert.Single(submissions.Images);
                    page.Composer.Input.Text = "/exit"; app.InjectKey(Key.Enter); stage++; return false;
                }
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
            return !deadline.IsCancellationRequested;
        });
        var exit = await commands.RunPreparedChatAsync(new ChatCommand(path, null, "standard", false), bootstrap, deadline.Token);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(4, stage); Assert.Equal(0, exit);
        Assert.DoesNotContain("usage_unavailable", errors.ToString());
        Assert.All(host.Requests, request => Assert.StartsWith("GET ", request));
        if (index < 0) Assert.DoesNotContain(host.Requests, request => request.Contains("/events"));
    }
}
