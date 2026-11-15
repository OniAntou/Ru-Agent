using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace RuApp.Services;

/// <summary>
/// <c>TransformToVisual</c> for code that runs inside layout events (SizeChanged,
/// LayoutUpdated) and timers. XAML throws <see cref="ArgumentException"/>
/// (E_INVALIDARG) when the two elements are not in one connected tree, which happens
/// while a container is being recycled or a view is leaving the window. Thrown from a
/// layout event, that exception is unhandled and XAML fails the process fast, so these
/// helpers report "no geometry this pass" instead; the next layout pass measures again.
/// </summary>
public static class VisualTransforms
{
    private const int MaximumTracedFailures = 20;
    private static int _tracedFailures;

    public static bool TryTransformPoint(
        UIElement element,
        UIElement? relativeTo,
        Point point,
        out Point result,
        [CallerMemberName] string caller = "")
    {
        try
        {
            result = element.TransformToVisual(relativeTo).TransformPoint(point);
            return true;
        }
        catch (ArgumentException)
        {
            TraceSkipped(caller);
            result = default;
            return false;
        }
    }

    public static bool TryTransformBounds(
        UIElement element,
        UIElement? relativeTo,
        Rect bounds,
        out Rect result,
        [CallerMemberName] string caller = "")
    {
        try
        {
            result = element.TransformToVisual(relativeTo).TransformBounds(bounds);
            return true;
        }
        catch (ArgumentException)
        {
            TraceSkipped(caller);
            result = default;
            return false;
        }
    }

    /// <summary>Bounded: a detached element can be asked about on every layout pass.</summary>
    private static void TraceSkipped(string caller)
    {
        if (Interlocked.Increment(ref _tracedFailures) > MaximumTracedFailures) return;
        GuiDiagnosticWriter.AppendTraceLine($"[Layout] TransformToVisual skipped in {caller}: elements not in one tree");
    }
}
