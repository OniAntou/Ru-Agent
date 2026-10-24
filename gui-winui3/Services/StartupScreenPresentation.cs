using RuApp.ViewModels;

namespace RuApp.Services;

/// <summary>Reader-facing startup states, without invented progress percentages.</summary>
public sealed record StartupScreenPresentation(string StatusText, bool IsReady, bool IsError)
{
    public static StartupScreenPresentation For(ShellStartupPhase phase, bool surfaceReady = true) => phase switch
    {
        ShellStartupPhase.Ready when !surfaceReady => new("Preparing your workspace…", false, false),
        ShellStartupPhase.Ready => new("Ready", true, false),
        ShellStartupPhase.RootMissing => new("The application folder could not be found.", true, true),
        ShellStartupPhase.Failed => new("The local service couldn't start. Open the app to review its status.", true, true),
        _ => new("Getting Ru ready…", false, false),
    };
}
