using RuAgent.Cli;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// Diagnostics follow the selected presentation and never leak into JSON stdout.
/// </summary>
public sealed class ChatDiagnosticAdapterTests
{
    [Fact]
    public async Task LineDiagnosticsReachTheTextSinkEvenIfAnOldBridgeExists()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var tgWriter = new TgTranscriptWriter(app, window.Transcript);
        // An inactive native bridge must not swallow line-adapter diagnostics.
        app.Dispose();

        var fallback = new StringWriter();
        var selected = AgentCommands.ResolveDiagnosticWriter(tgMode: false, tgWriter, fallback);
        Assert.Same(fallback, selected);
        await selected.WriteLineAsync("line diagnostic");

        Assert.Contains("line diagnostic", fallback.ToString());
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
