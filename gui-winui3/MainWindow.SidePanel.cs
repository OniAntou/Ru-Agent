using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp
{
    /// <summary>
    /// The title bar's side-surface toggle and update action sit left of the
    /// system caption buttons. Interactive title-bar content needs explicit
    /// passthrough regions, so the action group's rectangle is registered with the
    /// window's non-client input source and refreshed whenever the bar's layout
    /// changes.
    /// </summary>
    public sealed partial class MainWindow
    {
        private Windows.Graphics.RectInt32? _titleBarActionsRegion;

        private void InitializeSidePanelToggle()
        {
            if (AppTitleBar is null || SidePanelToggleButton is null)
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
                || SidePanelToggleButton is null)
            {
                return;
            }

            double scale = xamlRoot.RasterizationScale;
            double captionHeight = TitleBarLayout.PixelsToLayoutUnits(_appWindow.TitleBar.Height, scale);
            if (captionHeight > 0)
            {
                SidePanelToggleButton.Height = captionHeight;
            }

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
                || TitleBarActions is null
                || TitleBarActions.ActualWidth <= 0)
            {
                return;
            }

            var bounds = TitleBarActions.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(
                    0,
                    0,
                    TitleBarActions.ActualWidth,
                    TitleBarActions.ActualHeight));
            var region = TitleBarLayout.ToPhysicalRect(bounds, scale);
            if (_titleBarActionsRegion is { } previous && previous.Equals(region)) return;
            var source = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            source.SetRegionRects(
                NonClientRegionKind.Passthrough,
                new[] { region });
            _titleBarActionsRegion = region;
        }

        private void OnWindowActivatedForSidePanelToggle(object sender, WindowActivatedEventArgs args)
        {
            if (SidePanelToggleButton is null)
            {
                return;
            }

            bool isActive = args.WindowActivationState != WindowActivationState.Deactivated;
            Services.UiMotion.FadeOpacity(SidePanelToggleButton, isActive ? 1 : 0.55);
        }

        /// <summary>
        /// The chat reports whether its side panel can show and whether it is hidden;
        /// the toggle's enabled state and wording are bound to the shell.
        /// </summary>
        public void UpdateSidePanelToggleState(bool canToggle, bool isHidden)
        {
            Shell.SetSidePanelState(canToggle, isHidden);
        }

        private void OnSidePanelToggleClick(object sender, RoutedEventArgs e)
        {
            _chatView?.ToggleSidePanel();
        }
    }
}
