using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Pins two decisions the window-close path makes, both of which were invisible:
/// which thread the XAML teardown runs on, and what happens when a view throws
/// on its way out.
/// </summary>
public sealed class MainWindowTeardownSourceContractTests
{
    [Fact]
    public void Closing_the_window_disposes_the_views_on_the_ui_thread()
    {
        // The Host shutdown POST is I/O and runs on a background task, but the
        // views are XAML objects: disposing them from that thread throws
        // RPC_E_WRONG_THREAD and skips the rest of the teardown.
        string teardown = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "DisposeWindowResourcesAsync");

        Assert.Contains("RunOnDispatcherAsync(DisposeWindowResourcesOnUiThread)", teardown, StringComparison.Ordinal);
        Assert.DoesNotContain("_chatView.Dispose()", teardown, StringComparison.Ordinal);
        Assert.DoesNotContain("_logView?.Dispose()", teardown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_host_composition_survives_a_view_that_throws_while_tearing_down()
    {
        // The composition owns the Host process and its Worker/Broker Job. It used
        // to sit at the end of one try block, so any view failing to dispose left
        // those children running after the GUI was gone.
        string teardown = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "DisposeWindowResourcesAsync");
        string onUiThread = GuiSource.MethodBody(GuiSource.ViewText("MainWindow"), "DisposeWindowResourcesOnUiThread");

        Assert.Contains("DisposeSafely(\"composition\", _composition.Dispose)", teardown, StringComparison.Ordinal);
        Assert.DoesNotContain("_composition.Dispose()", onUiThread, StringComparison.Ordinal);
    }
}
