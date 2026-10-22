using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;

namespace RuApp.Controls;

public sealed partial class AgentSidePanelControl
{
    private uint? _resizePointerId;
    private double _resizeStartWidth;
    private double _resizeStartX;

    private void OnGripPointerEntered(object sender, PointerRoutedEventArgs e) => PanelDivider.Opacity = 0.9;
    private void OnGripPointerExited(object sender, PointerRoutedEventArgs e) => PanelDivider.Opacity = 0.6;

    private void OnGripPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (PanelHost.Visibility != Visibility.Visible || PanelHost.HorizontalAlignment != HorizontalAlignment.Right
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!PanelResizeGrip.CapturePointer(e.Pointer)) return;
        _resizePointerId = e.Pointer.PointerId;
        _resizeStartWidth = PanelHost.Width;
        _resizeStartX = e.GetCurrentPoint(this).Position.X;
        e.Handled = true;
    }

    private void OnGripPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizePointerId != e.Pointer.PointerId) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndPanelResize(); return; }
        SetPanelWidth(_resizeStartWidth + _resizeStartX - e.GetCurrentPoint(this).Position.X);
        e.Handled = true;
    }

    private void OnGripPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizePointerId == e.Pointer.PointerId) { EndPanelResize(); e.Handled = true; }
    }

    private void OnGripPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_resizePointerId == e.Pointer.PointerId) EndPanelResize();
    }

    private void EndPanelResize()
    {
        if (_resizePointerId is null) return;
        _resizePointerId = null;
        PanelResizeGrip.ReleasePointerCaptures();
        WidthCommitted?.Invoke(this, PanelResizeGrip.PanelWidth);
    }

    private void SetPanelWidth(double width)
    {
        width = AgentSidePanelPolicy.ClampWidth(width);
        PanelHost.Width = width;
        PanelResizeGrip.PanelWidth = width;
        WidthChanged?.Invoke(this, width);
    }

    private void OnPanelWidthRequested(object? sender, double width)
    {
        SetPanelWidth(width);
        WidthCommitted?.Invoke(this, width);
    }
}
