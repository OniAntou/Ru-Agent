using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using RuApp.Services;

namespace RuApp.Views
{
    /// <summary>
    /// Settings motion: the tab entrance and the thumb that glides between the
    /// choices of a segmented control. Every path goes through <see cref="UiMotion"/>,
    /// which falls back to the plain state change when Windows animations are off.
    /// </summary>
    public sealed partial class SettingsView
    {
        /// <summary>The thumbs that have been placed once; only those glide afterwards.</summary>
        private readonly HashSet<Border> _placedThumbs = [];

        /// <summary>
        /// Each thumb with the grid it slides in. The grid is named in markup: an
        /// element's <c>Parent</c> is not set yet while the view is being built.
        /// </summary>
        private readonly Dictionary<Border, Grid> _thumbTracks = [];

        private void InitializeSegmentThumbs()
        {
            foreach (var (thumb, track) in new[]
                     {
                         (ThemeThumb, ThemeTrack),
                         (ActivityThumb, ActivityTrack),
                         (UpdateChannelThumb, UpdateChannelTrack),
                     })
            {
                _thumbTracks[thumb] = track;
                // A resize, or the tab becoming visible, re-measures the segments:
                // the thumb snaps to the new geometry instead of gliding across it.
                track.SizeChanged += (_, _) => PlaceSegmentThumb(thumb, animate: false);
                PlaceSegmentThumb(thumb, animate: false);
            }
        }

        /// <summary>Moves a segmented control's thumb under the checked segment.</summary>
        private void PlaceSegmentThumb(Border thumb, bool animate = true)
        {
            if (!_thumbTracks.TryGetValue(thumb, out var track) || track.ActualWidth <= 0) return;
            var selected = track.Children
                .OfType<Panel>()
                .SelectMany(panel => panel.Children.OfType<ToggleButton>())
                .FirstOrDefault(segment => segment.IsChecked == true);
            if (selected is null || selected.ActualWidth <= 0)
            {
                thumb.Opacity = 0;
                return;
            }

            // Runs from track.SizeChanged: a throw here would fail the process fast.
            if (!VisualTransforms.TryTransformPoint(selected, track, new Windows.Foundation.Point(0, 0), out var origin)) return;
            var offset = origin.X;
            thumb.Width = selected.ActualWidth;
            thumb.Height = selected.ActualHeight;
            thumb.Opacity = 1;
            UiMotion.SlideX(thumb, offset, animate && _placedThumbs.Contains(thumb));
            _placedThumbs.Add(thumb);
        }

        /// <summary>
        /// A page arrives whole, and the heading fades in beside it.
        /// </summary>
        /// <remarks>
        /// Never animate the cards one by one: they carry a XAML <c>Translation</c> for
        /// their shadow, and enabling composition translation on them through
        /// <see cref="UiMotion.Enter"/> moved their hit-test bounds away from where they
        /// draw. After a tab switch every card but the first stopped taking clicks.
        /// The page's own panel has no XAML translation, so it is safe to move.
        /// </remarks>
        private void PlayTabEntrance(SettingsTab tab)
        {
            ScrollViewer panel = tab switch
            {
                SettingsTab.General => PanelGeneralScrollViewer,
                SettingsTab.Appearance => PanelAppearanceScrollViewer,
                SettingsTab.Models => PanelModelsScrollViewer,
                SettingsTab.Permissions => PanelRulesScrollViewer,
                SettingsTab.Shortcuts => PanelShortcutsScrollViewer,
                _ => PanelAboutScrollViewer,
            };

            if (panel.Content is FrameworkElement page)
            {
                UiMotion.Enter(page);
            }

            foreach (var heading in new FrameworkElement[] { CategoryTitleText, CategorySubtitleText })
            {
                heading.Opacity = 0;
                UiMotion.FadeOpacity(heading, 1);
            }
        }
    }
}
