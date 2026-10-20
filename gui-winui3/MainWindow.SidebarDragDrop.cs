using System;
using System.Linq;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Models;
using RuApp.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace RuApp;

public sealed partial class MainWindow
{
    private const string SidebarSessionDragFormat = "Ru.Sidebar.Session";
    private string? _dragSessionId;
    private ListViewItem? _dragSource;
    private ListViewItem? _dropHighlight;
    private DispatcherTimer? _sidebarDragTimer;
    private Point? _sidebarDragPoint;
    private string? _hoverGroupKey;
    private DateTimeOffset _hoverGroupSince;
    private sealed record SidebarDropTarget(string Key, string? Path, string? BeforeId, ListViewItem Container, bool InsertAbove);

    private void InitializeSidebarMotion()
    {
        SidebarSessionList.ItemContainerTransitions = UiTiming.AnimationsEnabled
            ? new TransitionCollection { new AddDeleteThemeTransition(), new RepositionThemeTransition() }
            : new TransitionCollection();
    }

    private void OnSidebarDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.Count != 1 || e.Items[0] is not SessionInfo session) { e.Cancel = true; return; }
        ResetSidebarDrag();
        _dragSessionId = session.Id;
        _dragSource = SidebarSessionList.ContainerFromItem(session) as ListViewItem;
        e.Data.SetData(SidebarSessionDragFormat, session.Id);
        e.Data.RequestedOperation = DataPackageOperation.Move;
        if (_dragSource is not null) SetSidebarOpacity(_dragSource, 0.45f);
        _sidebarDragTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _sidebarDragTimer.Tick -= OnSidebarDragTick;
        _sidebarDragTimer.Tick += OnSidebarDragTick;
        _sidebarDragTimer.Start();
    }

    private void OnSidebarDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs e) => ResetSidebarDrag();

    private void OnSidebarDragOver(object sender, DragEventArgs e)
    {
        if (_dragSessionId is null || !e.DataView.Contains(SidebarSessionDragFormat)) return;
        _sidebarDragPoint = e.GetPosition(ShellRoot);
        var target = FindSidebarDropTarget(_sidebarDragPoint.Value);
        e.AcceptedOperation = target is null ? DataPackageOperation.None : DataPackageOperation.Move;
        e.Handled = true;
        if (target is not null)
        {
            e.DragUIOverride.Caption = "Move to " + (target.Key == SidebarSessionOrganization.RecentsKey
                ? "Recents" : target.Key == SidebarSessionOrganization.CliKey ? "CLI Session" : WorkspaceSessionProjection.LabelForPath(target.Path!)) + " · sidebar only";
            e.DragUIOverride.IsCaptionVisible = true;
        }
        ShowSidebarDropTarget(target);
    }

    private SidebarDropTarget? FindSidebarDropTarget(Point point)
    {
        var container = VisualTreeHelper.FindElementsInHostCoordinates(point, ShellRoot)
            .OfType<ListViewItem>().FirstOrDefault(item => SidebarSessionList.ItemFromContainer(item) is not null);
        if (container is null || string.IsNullOrWhiteSpace(_dragSessionId)) return null;
        var row = SidebarSessionList.ItemFromContainer(container);
        var local = ShellRoot.TransformToVisual(container).TransformPoint(point);
        var above = local.Y < container.ActualHeight / 2;
        var intent = SidebarDragDropPresenter.Resolve(
            _dragSessionId,
            row!,
            above,
            Sessions.ToList(),
            _lastWorkspaceGroups,
            SidebarGroupFor);
        return intent is null ? null : new(intent.Key, intent.Path, intent.BeforeId, container, intent.InsertAbove);
    }

    private void ShowSidebarDropTarget(SidebarDropTarget? target)
    {
        ClearSidebarDropHighlight();
        if (target is null) { _hoverGroupKey = null; return; }
        if (_hoverGroupKey != target.Key) { _hoverGroupKey = target.Key; _hoverGroupSince = DateTimeOffset.UtcNow; }
        _dropHighlight = target.Container;
        _dropHighlight.BorderBrush = (Brush)Application.Current.Resources["RuAccentBrush"];
        _dropHighlight.BorderThickness = new Thickness(1);
        if (SidebarSessionList.ItemFromContainer(target.Container) is SessionInfo)
        {
            var origin = target.Container.TransformToVisual(SidebarListOverlay).TransformPoint(new Point());
            var y = origin.Y + (target.InsertAbove ? 0 : target.Container.ActualHeight);
            SidebarDropIndicator.Margin = new Thickness(8, Math.Max(0, y), 8, 0);
            SetSidebarOpacity(SidebarDropIndicator, 1);
        }
    }

    private void OnSidebarDragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(SidebarSessionList);
        if (point.X >= 0 && point.X <= SidebarSessionList.ActualWidth
            && point.Y >= 0 && point.Y <= SidebarSessionList.ActualHeight) return;
        _sidebarDragPoint = null;
        _hoverGroupKey = null;
        ClearSidebarDropHighlight();
    }

    private void OnSidebarDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (_dragSessionId is null || !e.DataView.Contains(SidebarSessionDragFormat)) return;
            var target = FindSidebarDropTarget(e.GetPosition(ShellRoot));
            if (target is null) return;
            e.AcceptedOperation = MoveSidebarSession(_dragSessionId, target.Key, target.Path, target.BeforeId)
                ? DataPackageOperation.Move : DataPackageOperation.None;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Sidebar move failed: {ex.Message}");
            SetSessionActionError("Could not move this session. Try again.");
        }
        finally { ResetSidebarDrag(); }
    }

    private bool MoveSidebarSession(string sessionId, string key, string? path, string? beforeId)
    {
        if (!SidebarSessionPresenter.TryMove(
                _sidebarOrganization, Sessions.ToList(), _sidebarOrganization.Current,
                sessionId, key, path, beforeId))
        {
            SetSessionActionError("Could not save the sidebar order. Try again.");
            return false;
        }
        _workspaceGroups.SetExpanded(key, true);
        _groupShowAll.Add(key);
        if (_activeSessionId == sessionId) _selectedWorkspaceKey = key;
        RenderWorkspaceSidebar();
        var row = _sidebarRows.OfType<SessionInfo>().FirstOrDefault(session => session.Id == sessionId);
        if (row is not null) SidebarSessionList.ScrollIntoView(row);
        return true;
    }

    private void OnSidebarDragTick(object? sender, object e)
    {
        try { AdvanceSidebarDrag(); }
        catch (Exception ex)
        {
            ResetSidebarDrag();
            GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Sidebar drag stopped: {ex.Message}");
        }
    }

    private void AdvanceSidebarDrag()
    {
        if (_sidebarDragPoint is not { } point || _dragSessionId is null) return;
        var local = ShellRoot.TransformToVisual(SidebarSessionList).TransformPoint(point);
        var scroll = FindSidebarScrollViewer(SidebarSessionList);
        if (scroll is not null)
        {
            var delta = local.Y < 28 ? -12 : local.Y > SidebarSessionList.ActualHeight - 28 ? 12 : 0;
            if (delta != 0) scroll.ChangeView(null, Math.Clamp(scroll.VerticalOffset + delta, 0, scroll.ScrollableHeight), null, true);
        }
        if (_hoverGroupKey is { } key && DateTimeOffset.UtcNow - _hoverGroupSince > TimeSpan.FromMilliseconds(450))
        {
            var collapsed = key is SidebarSessionOrganization.RecentsKey or SidebarSessionOrganization.CliKey
                ? _sidebarRows.OfType<SidebarSectionRow>().Any(row => row.Key == key && !row.IsExpanded)
                : _sidebarRows.OfType<WorkspaceGroupRow>().Any(row => row.Group.Key == key && !row.IsExpanded);
            if (collapsed) { _workspaceGroups.SetExpanded(key, true); RenderWorkspaceSidebar(); }
        }
        ShowSidebarDropTarget(FindSidebarDropTarget(point));
    }

    private static ScrollViewer? FindSidebarScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindSidebarScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }

    private static void SetSidebarOpacity(UIElement element, float opacity)
    {
        if (element.XamlRoot is null) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        if (!UiTiming.AnimationsEnabled) { visual.Opacity = opacity; return; }
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        var ease = visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1), new Vector2(0.3f, 1));
        animation.InsertKeyFrame(1, opacity, ease);
        animation.Duration = TimeSpan.FromMilliseconds(140);
        visual.StartAnimation("Opacity", animation);
    }

    private void ClearSidebarDropHighlight()
    {
        if (_dropHighlight is not null)
        {
            _dropHighlight.ClearValue(Control.BorderBrushProperty);
            _dropHighlight.ClearValue(Control.BorderThicknessProperty);
            _dropHighlight = null;
        }
        SetSidebarOpacity(SidebarDropIndicator, 0);
    }

    private void ResetSidebarDrag()
    {
        _sidebarDragTimer?.Stop();
        if (_dragSource is not null) SetSidebarOpacity(_dragSource, 1);
        _dragSource = null;
        _dragSessionId = null;
        _sidebarDragPoint = null;
        _hoverGroupKey = null;
        ClearSidebarDropHighlight();
    }
}
