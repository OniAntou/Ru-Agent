using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed class StartupScreenPresentationTests
{
    [Theory]
    [InlineData(ShellStartupPhase.NotStarted, false, false)]
    [InlineData(ShellStartupPhase.Starting, false, false)]
    [InlineData(ShellStartupPhase.Ready, true, false)]
    [InlineData(ShellStartupPhase.RootMissing, true, true)]
    [InlineData(ShellStartupPhase.Failed, true, true)]
    public void Startup_phases_have_honest_ready_and_error_states(ShellStartupPhase phase, bool ready, bool error)
    {
        var presentation = StartupScreenPresentation.For(phase);
        Assert.Equal(ready, presentation.IsReady);
        Assert.Equal(error, presentation.IsError);
        Assert.False(string.IsNullOrWhiteSpace(presentation.StatusText));
        Assert.DoesNotContain("%", presentation.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(".NET", presentation.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Backend_readiness_does_not_dismiss_before_the_chat_surface_exists()
    {
        var preparing = StartupScreenPresentation.For(ShellStartupPhase.Ready, surfaceReady: false);
        Assert.False(preparing.IsReady);
        Assert.False(preparing.IsError);
        Assert.Contains("workspace", preparing.StatusText, StringComparison.Ordinal);
        Assert.True(StartupScreenPresentation.For(ShellStartupPhase.Ready, surfaceReady: true).IsReady);
        var startup = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "InitializeAppAsync");
        Assert.True(startup.IndexOf("ContentFrame.Content = _chatView", StringComparison.Ordinal)
            < startup.IndexOf("_startupSurfaceReady = true", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_errors_name_the_problem_instead_of_claiming_readiness()
    {
        var missing = StartupScreenPresentation.For(ShellStartupPhase.RootMissing);
        var failed = StartupScreenPresentation.For(ShellStartupPhase.Failed);
        Assert.Contains("folder", missing.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start", failed.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ready", missing.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Ready", failed.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_uses_the_presentation_and_shutdown_disposes_the_overlay()
    {
        var shell = GuiSource.ViewText("MainWindow");
        Assert.Contains("StartupScreenPresentation.For", GuiSource.MethodBody(shell, "ApplyWelcomeStatus"), StringComparison.Ordinal);
        Assert.Contains("WelcomeOverlay?.Dispose()", GuiSource.MethodBody(shell, "OnWindowClosed"), StringComparison.Ordinal);
        Assert.Contains("WelcomeOverlay?.SetStatus", GuiSource.MethodBody(shell, "InitializeAppSafelyAsync"), StringComparison.Ordinal);
    }
}
