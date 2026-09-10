using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The opt-in layout diagnostics. They must be free when disabled and must never throw,
/// because they are called from the middle of a repaint.
/// </summary>
public sealed class TuiDebugTests
{
    [Fact]
    public void Log_IsANoOpWhenDisabled()
    {
        // The test process does not set RU_TUI_DEBUG, so this is the disabled path.
        Assert.False(TuiDebug.IsEnabled);

        TuiDebug.Log("layout", "this must not throw");

        Assert.False(TuiDebug.IsEnabled);
    }
}
