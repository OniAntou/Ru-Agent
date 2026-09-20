using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>
    /// The per-session token report: two headline figures, the split between input,
    /// output and cache, and one row per recent run.
    ///
    /// It is a panel rather than a chip because the question it answers is about a
    /// session, including one that is not the session on screen, while the composer
    /// only ever speaks for the conversation in front of the user. Everything it shows
    /// is measured against the session's own largest step: it has no idea which model
    /// each run used, and a share of a limit it guessed would be a number nobody could
    /// act on.
    /// </summary>
    public sealed partial class SessionUsagePanel : UserControl
    {
        private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable");
        private static readonly FontFamily MonoFont = new FontFamily("Consolas");

        public SessionUsagePanel()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Reads the session's runs and paints the panel.
        ///
        /// The read is fresh on every open, with no cache: the panel is opened
        /// deliberately, and a cached answer would be wrong the moment the run the user
        /// just watched finishes. The read itself is bounded by
        /// <see cref="SessionUsagePolicy.MaxReportedRuns"/> runs.
        /// </summary>
        public async Task LoadAsync(ApiService api, string sessionId, string? sessionTitle)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

            SessionTitleText.Text = string.IsNullOrWhiteSpace(sessionTitle) ? "Session" : sessionTitle;
            CoverageText.Text = "Reading the newest runs…";
            RowsPanel.Children.Clear();
            EmptyText.Visibility = Visibility.Collapsed;
            ShareBarGrid.Children.Clear();
            FooterText.Text = $"Reads the newest {SessionUsagePolicy.MaxReportedRuns} runs. Each run costs two requests, so the panel covers the tail of the session.";

            IReadOnlyList<SessionUsageRow>? rows;
            try
            {
                rows = await api.ListSessionUsageAsync(sessionId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Session usage read failed: {exception.Message}");
                rows = null;
            }

            Render(rows);
        }

        private void Render(IReadOnlyList<SessionUsageRow>? rows)
        {
            var summary = SessionUsagePolicy.Summarize(rows ?? Array.Empty<SessionUsageRow>());
            int count = rows?.Count ?? 0;

            CoverageText.Text = rows is null
                ? "Session usage is unavailable right now."
                : summary.HasAny
                    ? $"{summary.ReportedRuns} of {summary.Runs} runs reported what the provider counted"
                    : count == 0 ? "No runs yet." : "No provider report yet.";

            BilledValueText.Text = ModelContextSpecs.FormatTokens(summary.BilledTokens);
            PeakValueText.Text = ModelContextSpecs.FormatTokens(summary.PeakTotal);
            PeakCaptionText.Text = summary.HasAny
                ? "High-water mark: steps are never summed"
                : "Largest single step of the session";

            InputValueText.Text = ModelContextSpecs.FormatTokens(summary.TotalInput);
            OutputValueText.Text = ModelContextSpecs.FormatTokens(summary.TotalOutput);
            CacheValueText.Text = ModelContextSpecs.FormatTokens(summary.CacheRead + summary.CacheWrite);
            CacheLegendRow.Visibility = summary.HasCache ? Visibility.Visible : Visibility.Collapsed;
            PaintShareBar(summary);

            RunsHeaderText.Text = count switch
            {
                0 => "Runs",
                1 => "1 run",
                _ => $"{count} runs, newest first",
            };
            RunsHeaderText.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;

            RowsPanel.Children.Clear();
            // Newest first: the run the user just watched is the one they opened the
            // panel for, and counting down from it is how a log reads.
            for (int index = count - 1; index >= 0; index--)
            {
                RowsPanel.Children.Add(BuildRow(count - index, rows![index], summary.PeakTotal));
            }

            EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = rows is null
                ? "The session's runs could not be read. The host answered, but not with usage."
                : count == 0
                    ? "This session has no runs yet. Reports arrive with the first completed step."
                    : "No run in this session has reported usage yet.";
        }

        /// <summary>
        /// Splits the bar by share of measured tokens. A part that measured nothing gets
        /// no segment at all, so the bar never shows a sliver for a figure that is zero.
        /// </summary>
        private void PaintShareBar(SessionUsageSummary summary)
        {
            ShareBarGrid.Children.Clear();
            ShareBarGrid.ColumnDefinitions.Clear();

            AddSegment(summary.InputShare, "RuAccentBrush");
            AddSegment(summary.OutputShare, "RuVioletBrush");
            AddSegment(summary.CacheShare, "RuInfoBrush");

            void AddSegment(double share, string brushKey)
            {
                if (share <= 0)
                {
                    return;
                }

                ShareBarGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(share, GridUnitType.Star),
                });

                var segment = new Border
                {
                    Background = ThemeBrush(brushKey),
                    CornerRadius = new CornerRadius(3),
                };
                Grid.SetColumn(segment, ShareBarGrid.ColumnDefinitions.Count - 1);
                ShareBarGrid.Children.Add(segment);
            }
        }

        /// <summary>
        /// One run's row: its place in the list, a bar sized against the session's
        /// largest step, and the provider's counts. The row announces the whole line,
        /// so a screen reader does not have to stitch three columns together.
        /// </summary>
        private static FrameworkElement BuildRow(int position, SessionUsageRow row, int peak)
        {
            var ink = ThemeBrush("RuInkBrush");
            var muted = ThemeBrush("RuMutedBrush");

            var line = new Grid { MinHeight = 26, ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28, GridUnitType.Pixel) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88, GridUnitType.Pixel) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var place = new TextBlock
            {
                Text = $"#{position}",
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = muted,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };

            const double TrackInnerWidth = 86;
            var track = new Border
            {
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = ThemeBrush("RuCanvasBrush"),
                BorderBrush = ThemeBrush("RuLineBrush"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Grid
                {
                    Children =
                    {
                        new Border
                        {
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Background = ThemeBrush("RuAccentBrush"),
                            CornerRadius = new CornerRadius(3),
                            Width = TrackInnerWidth * SessionUsagePolicy.ShareOfPeak(
                                row.Usage?.ResolvedTotal ?? 0, peak) / 100.0,
                        },
                    },
                },
            };

            string detail = row.HasUsage
                ? $"in {ModelContextSpecs.FormatTokens(row.Usage!.InputTokens ?? 0)}"
                  + $" · out {ModelContextSpecs.FormatTokens(row.Usage.OutputTokens ?? 0)}"
                  + $" · total {ModelContextSpecs.FormatTokens(row.Usage.ResolvedTotal)}"
                : row.IsTerminal ? "no provider report" : "still running";

            var numbers = new TextBlock
            {
                Text = detail,
                FontFamily = MonoFont,
                FontSize = 11.5,
                Foreground = row.HasUsage ? ink : muted,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(track, 1);
            Grid.SetColumn(numbers, 2);
            line.Children.Add(place);
            line.Children.Add(track);
            line.Children.Add(numbers);
            AutomationProperties.SetName(line, $"Run {position}: {detail}");
            return line;
        }

        private static Brush ThemeBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
