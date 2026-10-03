using RuAgent.Cli;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// I4: the tiny-terminal handover disposes the Terminal.Gui app while the bridge writer
/// still references it, so diagnostics must fall back to the real console writer.
/// </summary>
public sealed class TgHandoverDiagnosticsTests
{
    [Fact]
    public async Task DiagnosticLine_AfterHandover_ReachesTheSequentialWriter()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var tgWriter = new TgTranscriptWriter(app, window.Transcript);
        // The handover disposes the app; from here on a write through the bridge vanishes.
        app.Dispose();

        var fallback = new StringWriter();
        var selected = AgentCommands.ResolveDiagnosticWriter(tgMode: false, tgWriter, fallback);
        Assert.Same(fallback, selected);
        await selected.WriteLineAsync("post-handover diagnostic");

        Assert.Contains("post-handover diagnostic", fallback.ToString());
    }

    [Fact]
    public void DiagnosticWriter_WhileTgOwnsTheFrame_KeepsTheBridge()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var tgWriter = new TgTranscriptWriter(app, window.Transcript);
        var fallback = new StringWriter();

        Assert.Same(tgWriter, AgentCommands.ResolveDiagnosticWriter(tgMode: true, tgWriter, fallback));
        Assert.Same(fallback, AgentCommands.ResolveDiagnosticWriter(tgMode: true, tgWriter: null, fallback));
    }
}
