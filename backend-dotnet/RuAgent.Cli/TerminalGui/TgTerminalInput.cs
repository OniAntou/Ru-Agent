using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Selects the console input path without losing split Unicode scalars.</summary>
public static class TgTerminalInput
{
    public static void Initialize(IApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        // Terminal.Gui 2.5's Windows ANSI input decodes each byte read independently.
        // The .NET driver retains UTF-16 console characters through bracketed-paste
        // parsing, including surrogate pairs split across native console records.
        // Both drivers keep the same Terminal.Gui views and ANSI terminal protocol.
        app.Init(OperatingSystem.IsWindows() ? DriverRegistry.Names.DOTNET : DriverRegistry.Names.ANSI);
    }
}
