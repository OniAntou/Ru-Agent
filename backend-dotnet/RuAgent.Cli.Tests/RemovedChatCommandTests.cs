using System.Net;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class RemovedChatCommandTests
{
    [Theory]
    [InlineData("/attach")]
    [InlineData("/attachments")]
    [InlineData("/attachment")]
    [InlineData("/paste")]
    [InlineData("/detach")]
    [InlineData("/dettach")]
    [InlineData("/continue")]
    [InlineData("/quit")]
    [InlineData("/ATTACH \"C:\\workspace\\notes.txt\"")]
    [InlineData("/detach --all")]
    [InlineData("/continue run_not_adopted")]
    [InlineData("/skills")]
    [InlineData("/skills add-root C:\\skills")]
    [InlineData("/skill review")]
    [InlineData("/SKILL review inspect src/main.cs")]
    public async Task RemovedCommands_AreUndiscoverableAndCannotExecute(string command)
    {
        var slashCommand = command.Split(' ', 2)[0].ToLowerInvariant();
        command = command.Trim();
        Assert.DoesNotContain(ChatCommandCatalog.KnownCommands, entry => entry.Command == slashCommand);
        Assert.Empty(ChatCommandCatalog.FindMatches(slashCommand));
        Assert.Null(ChatCommandCatalog.AutoComplete(slashCommand, ChatCommandCatalog.KnownCommands.Select(entry => entry.Command)));
        Assert.DoesNotContain(slashCommand, string.Join('\n', TuiSurfaces.Help(160)), StringComparison.Ordinal);
        var palette = new TgCommandPalette();
        palette.Reset();
        Assert.DoesNotContain(palette.Matches, entry => entry.Command == slashCommand);
        Assert.False(ChatCommandCatalog.IsReportCommand(slashCommand));

        var process = System.Diagnostics.Process.GetCurrentProcess();
        var host = PublicHostSession.FromDescriptor(new RuntimeDescriptor(
            1, "http://127.0.0.1:8000/", "synthetic-command-removal-bearer", process.Id,
            process.StartTime.ToUniversalTime().ToString("O"), new string('a', 32), DateTimeOffset.UtcNow.ToString("O")));
        using var handler = new ReadOnlyHandler();
        using var http = new HttpClient(handler);
        var output = new StringWriter();
        var errors = new StringWriter();
        using var input = new StringReader(command + "\n/status\n/exit\n");
        using var commands = new AgentCommands(host, http, output, errors, input);

        var exit = await commands.ChatAsync(new ChatCommand("C:\\workspace", "sess_123", "standard", false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("unknown_command: " + slashCommand, errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("sess_123", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Goodbye!", output.ToString(), StringComparison.Ordinal);
        // Opening an existing chat activates its session; removed commands must add no mutations.
        Assert.Equal(["POST /sessions/sess_123/activate"], handler.Mutations);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "unfinished draft")]
    [InlineData(true, "")]
    [InlineData(true, "unfinished draft")]
    public void IdleEscape_DoesNotCloseChatOrLoseDraft(bool transcriptFocus, string draft)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        window.Composer.Input.Text = draft;
        var reachedAfterEscape = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            if (transcriptFocus)
            {
                window.Transcript.CanFocus = true;
                Assert.True(window.Transcript.SetFocus());
                Assert.True(window.Transcript.HasFocus);
            }
            app.InjectKey(Key.Esc);
            app.InjectKey(Key.Esc);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            reachedAfterEscape = true;
            Assert.False(loop.Completion.IsCompleted);
            Assert.False(window.IsCommandPaletteOpen);
            Assert.Equal(draft, window.Composer.Input.Text?.ToString());
            app.RequestStop();
            return false;
        });
        app.Run(window);
        Assert.True(reachedAfterEscape, "Escape stopped the app before its next event-loop callback.");
    }

    [Theory]
    [InlineData("palette")]
    [InlineData("slash")]
    [InlineData("document")]
    [InlineData("approval")]
    public void DismissingAnOverlay_ThenPressingEscapeAgain_KeepsChatOpen(string overlay)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var reachedAfterEscape = false;
        app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
        {
            window.FocusComposer();
            switch (overlay)
            {
                case "palette": window.OpenCommandPalette(); break;
                case "slash": window.Composer.Input.Text = "/"; window.ShowSlashDropdown(); break;
                case "document": window.ShowDocument("status", ["session sess_123"]); break;
                case "approval": window.ShowApproval("synthetic approval"); break;
            }
            app.InjectKey(Key.Esc);
            app.InjectKey(Key.Esc);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(350), () =>
        {
            reachedAfterEscape = true;
            Assert.False(loop.Completion.IsCompleted);
            Assert.False(window.IsCommandPaletteOpen);
            Assert.False(window.IsSlashDropdownOpen);
            Assert.False(window.IsDocumentPanelOpen);
            Assert.False(window.IsModalPanelOpen);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        Assert.True(reachedAfterEscape, "Repeated Escape quit the chat after closing an overlay.");
    }

    private sealed class ReadOnlyHandler : HttpMessageHandler
    {
        public List<string> Mutations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) Mutations.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
    }
}
