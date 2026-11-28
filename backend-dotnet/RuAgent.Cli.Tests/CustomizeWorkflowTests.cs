using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using RuAgent.Cli.TerminalGui;
using RuAgent.Client;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class CustomizeWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillsSelectAnActionWithoutSendingAChatPrompt(bool useSkill)
    {
        var workspace = Path.GetTempPath();
        var enabled = true;
        using var host = new CustomizeHost((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/agent/v1/skills/settings") enabled = false;
            if (request.RequestUri!.AbsolutePath.StartsWith("/agent/v1/skills", StringComparison.Ordinal))
                return new SkillCatalogResponse(
                    [new("review", "Review", "Review a change", "skills/review/SKILL.md", "skills/review", "1",
                        enabled, true, true, [], null)], [], false, 1);
            return new { status = "ok" };
        });

        await RunCustomizeAsync(host, workspace, "/customize skills", (stage, app, page, screen) =>
        {
            if (stage == 1 && page.IsModalPanelOpen && screen.Contains("On · Automatic"))
                app.InjectKey(Key.Enter);
            else if (stage == 2 && page.IsModalPanelOpen && screen.Contains("Use in chat"))
                app.InjectKey(useSkill ? Key.Enter : Key.D2);
            else if (stage == 3 && useSkill && page.Composer.Input.Text == "Use the review skill.")
                return true;
            else if (stage == 3 && !useSkill && page.IsModalPanelOpen && screen.Contains("Review") && screen.Contains("Off"))
            {
                app.InjectKey(Key.Esc);
                return true;
            }
            else return null;
            return false;
        });

        Assert.DoesNotContain(host.Requests, r => r.Contains(" /agent/v1/runs"));
        if (useSkill)
            Assert.DoesNotContain(host.Requests, r => r.StartsWith("POST ", StringComparison.Ordinal));
        else
        {
            var update = Assert.Single(host.Requests, r => r.StartsWith("POST /agent/v1/skills/settings ", StringComparison.Ordinal));
            Assert.Contains("\"action\":\"set_enabled\"", update);
            Assert.Contains("\"id\":\"review\"", update);
            Assert.Contains("\"enabled\":false", update);
        }
    }

    // A real Terminal.Gui loop against an isolated fake Host. The driver callback
    // advances only after the expected view has drawn and the current key is consumed.
    private static async Task RunCustomizeAsync(CustomizeHost host, string workspace, string command,
        Func<int, IApplication, TgChatWindow, string, bool?> drive)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build();
        using var http = new HttpClient(host);
        using var errors = new StringWriter();
        using var commands = new AgentCommands(ChatTestSession.Value, http, TextWriter.Null, errors);
        var writer = new TgTranscriptWriter(app, page.Transcript);
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, workspace, workspace,
            string.Empty, "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stage = 0;
        var finished = false;
        var lastScreen = "";
        Exception? failure = null;
        app.AddTimeout(TimeSpan.FromMilliseconds(125), () =>
        {
            try
            {
                lastScreen = string.Join('\n', TgSnapshot.Capture(app));
                if (stage == 0)
                {
                    page.FocusComposer();
                    page.Composer.Input.Text = command;
                    app.InjectKey(Key.Enter);
                    stage++;
                }
                else if (finished)
                {
                    if (page.HasBlockingOverlay) return !deadline.IsCancellationRequested;
                    page.Composer.Input.Text = "/exit";
                    app.InjectKey(Key.Enter);
                    return false;
                }
                else
                {
                    var decision = drive(stage, app, page, lastScreen);
                    if (decision is true) finished = true;
                    else if (decision is false) stage++;
                }
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
            return !deadline.IsCancellationRequested;
        });
        await commands.RunPreparedChatAsync(new ChatCommand(workspace, null, "standard", false), bootstrap, deadline.Token);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(finished, $"Customize stopped at stage {stage}. {errors}\n{lastScreen}");
    }

    private sealed class CustomizeHost(Func<HttpRequestMessage, string, object> handle) : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue($"{request.Method} {request.RequestUri!.AbsolutePath} {body}");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(handle(request, body)) };
        }
    }
}
