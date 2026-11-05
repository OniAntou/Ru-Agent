namespace RuAgent.Broker.Tools.Computer;

public enum PointerButton
{
    Left,
    Right,
    Middle,
}

/// <summary>One UI Automation element as the controller read it (ADR 0031, amended 2026-11-05).</summary>
public sealed record DesktopElement(string Role, string Name, PixelRect Bounds, bool Offscreen, bool Enabled);

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

    /// <summary>
    /// Drags while resolving authorization for each pointer event. The callback may return
    /// different roots along the path when both applications have separate session grants.
    /// </summary>
    void Drag(int fromX, int fromY, int toX, int toY, Func<int, int, nint> authorizeTargetAt,
        CancellationToken cancellationToken);

    /// <summary>Scrolls while resolving authorization immediately before every pointer event.</summary>
    void Scroll(int x, int y, int deltaX, int deltaY, Func<int, int, nint> authorizeTargetAt);

    /// <summary>Types; before each character the foreground root window must still be <paramref name="expectedRoot"/>.</summary>
    void TypeText(string text, nint expectedRoot, CancellationToken cancellationToken);

    /// <summary>Presses a chord when the foreground root window is still <paramref name="expectedRoot"/>.</summary>
    void PressChord(KeyChord chord, nint expectedRoot);

    /// <summary>
    /// The UI Automation elements of one top-level window in its control view, unfiltered:
    /// the caller decides which are visible and allowed. Bounds are physical pixels.
    /// </summary>
    IReadOnlyList<DesktopElement> Elements(nint window, CancellationToken cancellationToken);

    /// <summary>
    /// Processes whose windows host Ru's terminal UI: every ancestor of a running Ru CLI,
    /// except the shell and system processes. Their windows are never targets.
    /// </summary>
    IReadOnlySet<int> RuHostProcessIds();
}
