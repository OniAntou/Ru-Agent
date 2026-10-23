using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using RuApp.Services;

namespace RuApp;

public sealed partial class MainWindow
{
    private void OnSidebarSectionToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Button { Tag: string key }) Sidebar.ToggleSection(key);
    }

    private readonly SidebarLayoutState _sidebarLayout = new();
    private uint? _sidebarResizePointerId;
    private double _sidebarResizeStartX;
    private double _sidebarResizeStartWidth;

    private void ApplyResponsiveShell(double width) => ApplySidebarLayout();

    private void ApplySidebarLayout()
    {
        SidebarColumn.MaxWidth = ResponsiveLayoutPolicy.MaximumSidebarWidth;
        SidebarColumn.MinWidth = _sidebarLayout.IsHidden ? 0 : ResponsiveLayoutPolicy.MinimumSidebarWidth;
        SidebarColumn.Width = new GridLength(_sidebarLayout.VisibleWidth);
        NavigationSidebar.Visibility = _sidebarLayout.IsHidden ? Visibility.Collapsed : Visibility.Visible;
        // Give the UserControl a real hit rectangle; negative margins in a 1px
        // column are clipped by UserControl and leave a nearly unclickable grip.
        SidebarDividerColumn.Width = new GridLength(_sidebarLayout.IsHidden ? 0 : SidebarResizer.Width);
        SidebarResizer.Visibility = NavigationSidebar.Visibility;
        SidebarResizer.SidebarWidth = _sidebarLayout.ExpandedWidth;
        string label = _sidebarLayout.IsHidden ? "Show workspace sidebar" : "Hide workspace sidebar";
        AutomationProperties.SetName(NavigationSidebarToggleButton, label);
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(NavigationSidebarToggleButton, label);
    }

    private void SetSidebarWidth(double width)
    {
        if (_sidebarLayout.IsHidden) return;
        _sidebarLayout.SetWidth(width);
        ApplySidebarLayout();
    }

    private void NudgeSidebarWidth(double delta) => SetSidebarWidth(_sidebarLayout.ExpandedWidth + delta);

    private void OnNavigationSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        if (SettingsModalOverlay.Visibility == Visibility.Visible) return;
        EndSidebarResize();
        _sidebarLayout.Toggle();
        ApplySidebarLayout();
        if (_sidebarLayout.IsHidden) NavigationSidebarToggleButton.Focus(FocusState.Programmatic);
    }

    private void OnSidebarWidthRequested(object? sender, double width) => SetSidebarWidth(width);

    private void Resizer_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_sidebarLayout.IsHidden || !e.GetCurrentPoint(RootLayoutGrid).Properties.IsLeftButtonPressed) return;
        if (!SidebarResizer.CapturePointer(e.Pointer)) return;
        _sidebarResizePointerId = e.Pointer.PointerId;
        _sidebarResizeStartX = e.GetCurrentPoint(RootLayoutGrid).Position.X;
        _sidebarResizeStartWidth = _sidebarLayout.ExpandedWidth;
        e.Handled = true;
    }

    private void Resizer_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_sidebarResizePointerId != e.Pointer.PointerId) return;
        var point = e.GetCurrentPoint(RootLayoutGrid);
        if (!point.Properties.IsLeftButtonPressed) { EndSidebarResize(); return; }
        SetSidebarWidth(_sidebarResizeStartWidth + point.Position.X - _sidebarResizeStartX);
        e.Handled = true;
    }

    private void Resizer_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_sidebarResizePointerId != e.Pointer.PointerId) return;
        SetSidebarWidth(_sidebarResizeStartWidth + e.GetCurrentPoint(RootLayoutGrid).Position.X - _sidebarResizeStartX);
        EndSidebarResize();
        e.Handled = true;
    }

    private void Resizer_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_sidebarResizePointerId == e.Pointer.PointerId) EndSidebarResize();
    }

    private void Resizer_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => Resizer_PointerCanceled(sender, e);
    private void Resizer_Unloaded(object sender, RoutedEventArgs e) => EndSidebarResize();

    private void EndSidebarResize()
    {
        if (_sidebarResizePointerId is null) return;
        _sidebarResizePointerId = null;
        SidebarResizer.ReleasePointerCaptures();
    }

    private void Resizer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        EndSidebarResize();
        SetSidebarWidth(ResponsiveLayoutPolicy.DefaultSidebarWidth);
        e.Handled = true;
    }

}
