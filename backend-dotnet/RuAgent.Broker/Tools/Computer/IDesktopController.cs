namespace RuAgent.Broker.Tools.Computer;

public enum PointerButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// The operating-system seam for computer use. Coordinates are physical
/// virtual-desktop pixels. Implementations are called on one thread per action.
/// </summary>
public interface IDesktopController
{
    /// <summary>False while the session is locked or a secure desktop is shown.</summary>
    bool IsInputDesktopAvailable();

    /// <summary>Monitors with the primary one first, then left to right, top to bottom.</summary>
    IReadOnlyList<DesktopMonitor> Monitors();

    /// <summary>Visible, uncloaked, non-minimized top-level windows from the top of the z-order down.</summary>
    IReadOnlyList<DesktopWindow> Windows();

    /// <summary>A top-down 32-bit BGRA copy of the given rectangle.</summary>
    byte[] Capture(PixelRect bounds);

    DesktopWindow? RootWindowAt(int x, int y);

    DesktopWindow? ForegroundWindow();

    /// <summary>The topmost window of an app in z-order, minimized windows included.</summary>
    DesktopWindow? TopWindowOf(string appName);

    /// <summary>Restores and activates a window; true when it is the foreground window afterwards.</summary>
    bool BringToForeground(nint handle);

    /// <summary>True when the process runs at a higher integrity than Ru, which Windows silently shields from input.</summary>
    bool IsShieldedFromInput(int processId);

    void MovePointer(int x, int y);

    /// <summary>Clicks; before each click the root window under the point must still be <paramref name="expectedRoot"/>.</summary>
    void Click(int x, int y, PointerButton button, int count, nint expectedRoot);

    void Drag(int fromX, int fromY, int toX, int toY, CancellationToken cancellationToken);

    void Scroll(int x, int y, int deltaX, int deltaY);

    /// <summary>Types; before each character the foreground root window must still be <paramref name="expectedRoot"/>.</summary>
    void TypeText(string text, nint expectedRoot, CancellationToken cancellationToken);

    /// <summary>Presses a chord when the foreground root window is still <paramref name="expectedRoot"/>.</summary>
    void PressChord(KeyChord chord, nint expectedRoot);

    /// <summary>
    /// Processes whose windows host Ru's terminal UI: every ancestor of a running Ru CLI,
    /// except the shell and system processes. Their windows are never targets.
    /// </summary>
    IReadOnlySet<int> RuHostProcessIds();
}
