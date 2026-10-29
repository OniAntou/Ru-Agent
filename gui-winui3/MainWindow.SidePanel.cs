using System;
using System.Collections.Generic;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp
{
    /// <summary>
    /// The title bar keeps the workspace-sidebar toggle at the left edge and the
    /// update action beside the caption buttons. Register separate passthrough
    /// regions so the space between them remains available for dragging.
    /// </summary>
    public sealed partial class MainWindow
    {
        private Windows.Graphics.RectInt32? _titleBarActionsRegion;
        private Windows.Graphics.RectInt32? _navigationSidebarRegion;

        private void InitializeSidePanelToggle()
        {
            if (AppTitleBar is null || NavigationSidebarToggleButton is null)
            {
                return;
            }

            AppTitleBar.Loaded += (_, _) => UpdateTitleBarToggleLayout();
            AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarToggleLayout();
            // Position-only layout changes need a fresh hit region; cache physical
            // bounds so unrelated chat layout does not re-register the region.
            AppTitleBar.LayoutUpdated += (_, _) =>
            {
                if (AppTitleBar.XamlRoot is { } root)
                    ApplySidePanelToggleRegion(root.RasterizationScale);
            };
            Activated += OnWindowActivatedForSidePanelToggle;
        }

        private void UpdateTitleBarToggleLayout()
        {
            if (_appWindow is null
                || AppTitleBar?.XamlRoot is not { } xamlRoot
                || NavigationSidebarToggleButton is null)
            {
                return;
            }

            double scale = xamlRoot.RasterizationScale;
            double captionHeight = TitleBarLayout.PixelsToLayoutUnits(_appWindow.TitleBar.Height, scale);
            if (captionHeight > 0)
            {
                NavigationSidebarToggleButton.Height = captionHeight;
                TitleBarActions.Height = captionHeight;
            }

            NavigationSidebarToggleButton.Margin = new Thickness(
                TitleBarLayout.InsetInLayoutUnits(_appWindow.TitleBar.LeftInset, scale), 0, 0, 0);
            TitleBarActions.Margin = new Thickness(
                0,
                0,
                TitleBarLayout.InsetInLayoutUnits(_appWindow.TitleBar.RightInset, scale),
                0);
            ApplySidePanelToggleRegion(scale);
        }

        private void ApplySidePanelToggleRegion(double scale)
        {
            if (_appWindow is null
                || NavigationSidebarToggleButton is null
                || NavigationSidebarToggleButton.ActualWidth <= 0)
            {
                return;
            }

            var navigation = RegionFor(NavigationSidebarToggleButton);
            var update = TitleBarUpdateButton.Visibility == Visibility.Visible ? RegionFor(TitleBarActions) : null;
            if (Nullable.Equals(_navigationSidebarRegion, navigation) && Nullable.Equals(_titleBarActionsRegion, update)) return;
            var regions = new List<Windows.Graphics.RectInt32>();
            if (navigation is { } left) regions.Add(left);
            if (update is { } right) regions.Add(right);
            var source = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            source.SetRegionRects(
                NonClientRegionKind.Passthrough,
                regions.ToArray());
            _navigationSidebarRegion = navigation;
            _titleBarActionsRegion = update;

            Windows.Graphics.RectInt32? RegionFor(FrameworkElement element)
            {
                if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return null;
                var bounds = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(
                    0, 0, element.ActualWidth, element.ActualHeight));
                return TitleBarLayout.ToPhysicalRect(bounds, scale);
            }
        }

        private void OnWindowActivatedForSidePanelToggle(object sender, WindowActivatedEventArgs args)
        {
            if (NavigationSidebarToggleButton is null)
            {
                return;
            }

            bool isActive = args.WindowActivationState != WindowActivationState.Deactivated;
            Services.UiMotion.FadeOpacity(NavigationSidebarToggleButton, isActive ? 1 : 0.55);
        }

        /// <summary>
        /// The chat reports whether its side panel can show and whether it is hidden;
        /// keep the command palette in sync with the conversation header control.
        /// </summary>
        public void UpdateSidePanelToggleState(bool canToggle, bool isHidden)
        {
            Shell.SetSidePanelState(canToggle, isHidden);
        }
    }
}
