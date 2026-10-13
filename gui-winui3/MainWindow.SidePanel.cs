using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using RuApp.Services;

namespace RuApp
{
    /// <summary>
    /// The title bar's side-surface toggle: the one control that hides and
    /// restores the whole right side surface, parked immediately left of the
    /// system caption buttons. Interactive title-bar content needs explicit
    /// passthrough regions, so the button's rectangle is registered with the
    /// window's non-client input source and refreshed whenever the bar's layout
    /// changes.
    /// </summary>
    public sealed partial class MainWindow
    {
        private void InitializeSidePanelToggle()
        {
            if (AppTitleBar is null || SidePanelToggleButton is null)
            {
                return;
            }

            AppTitleBar.Loaded += (_, _) => UpdateTitleBarToggleLayout();
            AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarToggleLayout();
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

            SidePanelToggleButton.Margin = new Thickness(
                0,
                0,
                TitleBarLayout.InsetInLayoutUnits(_appWindow.TitleBar.RightInset, scale),
                0);
            ApplySidePanelToggleRegion(scale);
        }

        private void ApplySidePanelToggleRegion(double scale)
        {
            if (_appWindow is null
                || SidePanelToggleButton is null
                || SidePanelToggleButton.ActualWidth <= 0)
            {
                return;
            }

            var bounds = SidePanelToggleButton.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(
                    0,
                    0,
                    SidePanelToggleButton.ActualWidth,
                    SidePanelToggleButton.ActualHeight));
            var source = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            source.SetRegionRects(
                NonClientRegionKind.Passthrough,
                new[] { TitleBarLayout.ToPhysicalRect(bounds, scale) });
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
        /// Keeps the toggle honest about what it can do: enabled only while the
        /// surface can show, and worded for the action it will take.
        /// </summary>
        public void UpdateSidePanelToggleState(bool canToggle, bool isHidden)
        {
            if (SidePanelToggleButton is null)
            {
                return;
            }

            SidePanelToggleButton.IsEnabled = canToggle;
            string name = isHidden ? "Show side panel" : "Hide side panel";
            ToolTipService.SetToolTip(SidePanelToggleButton, name);
            AutomationProperties.SetName(SidePanelToggleButton, name);
        }

        private void OnSidePanelToggleClick(object sender, RoutedEventArgs e)
        {
            _chatView?.ToggleSidePanel();
        }
    }
}
