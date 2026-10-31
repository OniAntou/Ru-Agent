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
        RunNativeFrame(app, window);
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

        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            Assert.Same(tgWriter, AgentCommands.ResolveDiagnosticWriter(tgMode: true, tgWriter, fallback));
            Assert.Same(fallback, AgentCommands.ResolveDiagnosticWriter(tgMode: true, tgWriter: null, fallback));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    private static void RunNativeFrame(IApplication app, TgChatWindow window)
    {
        // Immediate disposal after Init can race the Windows ANSI input startup.
        // Match the native lifecycle before testing an inactive bridge.
        app.AddTimeout(TimeSpan.FromMilliseconds(100), () =>
        {
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
