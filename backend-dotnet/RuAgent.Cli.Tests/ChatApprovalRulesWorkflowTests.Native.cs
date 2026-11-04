using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed partial class ChatApprovalRulesWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_preset_rules_support_details_escape_and_explicit_removal(bool confirm)
    {
        using IApplication app = Application.Create(); app.Init(DriverRegistry.Names.ANSI);
        using var page = TgChatWindow.Build();
        using var host = new Host(); using var http = new HttpClient(host);
        using var errors = new StringWriter();
        using var commands = new AgentCommands(Session, http, TextWriter.Null, errors);
        var writer = new TgTranscriptWriter(app, page.Transcript);
        var path = Path.GetTempPath();
        host.Metadata = [new { workspace_path = path, workspace_id = WorkspaceId }];
        var bootstrap = new AgentCommands.ChatBootstrapContext(new ChatSubmissionState(), writer, path, path, string.Empty,
            "OpenAI", "gpt-4o", "high", null, ChatPresentationMode.Native, true, writer, page, app);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Exception? failure = null; var stage = 0;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            try
            {
                if (app.TopRunnableView is not TgChatWindow) return true;
                var screen = string.Join('\n', TgSnapshot.Capture(app));
                if (stage == 0)
                {
                    page.FocusComposer(); page.Composer.Input.Text = "/preset"; app.InjectKey(Key.Enter); stage++;
                }
                else if (stage == 1 && page.IsModalPanelOpen && screen.Contains("Remembered rules"))
                {
                    // standard is the current selection; the fourth entry is management, not a preset.
                    app.InjectKey(Key.CursorDown); app.InjectKey(Key.CursorDown); app.InjectKey(Key.Enter); stage++;
                }
                else if (stage == 2 && page.IsModalPanelOpen && screen.Contains("enter details"))
                { app.InjectKey(Key.Enter); stage++; }
                else if (stage == 3 && screen.Contains("View details"))
                { app.InjectKey(Key.Enter); stage++; }
                else if (stage == 4 && page.IsDocumentPanelOpen)
                { Assert.Contains("Saved approval rule", screen); app.InjectKey(Key.Esc); stage++; }
                else if (stage == 5 && page.IsModalPanelOpen && screen.Contains("View details"))
                { app.InjectKey(Key.CursorDown); app.InjectKey(Key.Enter); stage++; }
                else if (stage == 6 && screen.Contains("Remove saved allowance?"))
                {
                    Assert.Contains("Keep rule", screen);
                    if (confirm) { app.InjectKey(Key.CursorDown); app.InjectKey(Key.Enter); }
                    else app.InjectKey(Key.Esc);
                    stage++;
                }
                else if (stage == 7)
                {
                    if (confirm)
                    {
                        if (!page.IsModalPanelOpen || !screen.Contains("enter details")) return true;
                        Assert.Equal(GlobalId, Assert.Single(host.Deletes)); app.InjectKey(Key.Esc);
                    }
                    else { if (page.IsModalPanelOpen) return true; Assert.Empty(host.Deletes); }
                    stage++;
                }
                else if (stage == 8 && !page.IsModalPanelOpen)
                {
                    page.Composer.Input.Text = "/exit"; app.InjectKey(Key.Enter); stage++; return false;
                }
            }
            catch (Exception error) { failure = error; app.RequestStop(); return false; }
            return !deadline.IsCancellationRequested;
        });
        var exit = await commands.RunPreparedChatAsync(new ChatCommand(path, null, "standard", false), bootstrap, deadline.Token);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.Equal(9, stage); Assert.Equal(0, exit);
        Assert.DoesNotContain("rules_unavailable", errors.ToString());
        Assert.DoesNotContain(host.Requests, request => request.StartsWith("POST "));
        Assert.Single(host.Project);
        Assert.Equal(confirm ? 0 : 1, host.Global.Count);
    }
}
