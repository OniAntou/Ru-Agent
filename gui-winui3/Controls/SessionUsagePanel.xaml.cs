using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>
    /// The per-session token report: what the session was billed for, where the measured
    /// tokens went, and one ledger entry per recent run.
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
        /// <summary>
        /// How long the body takes to arrive once the read lands. The only motion in the
        /// panel: it says the rows are new, and it is skipped entirely when the system
        /// has animations turned off.
        /// </summary>
        private static readonly TimeSpan LedgerArrival = TimeSpan.FromMilliseconds(180);

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
            CoverageText.Text = "reading…";
            ElapsedValueText.Text = "—";
            PeakCaptionText.Text = "Largest single step";
            RowsPanel.Children.Clear();
            EmptyText.Visibility = Visibility.Collapsed;

            // Clear the figures too. Leaving the previous session's totals on screen
            // under "reading…" states a number that belongs to a different session, and
            // a re-open is exactly when nobody is looking.
            BilledValueText.Text = "—";
            PeakValueText.Text = "—";
            InputValueText.Text = "—";
            OutputValueText.Text = "—";
            CacheReadValueText.Text = "—";
            CacheWriteValueText.Text = "—";
            PaintShareCell(InputShareBar, InputValueText, "—", 0, "RuAccentBrush", false);
            PaintShareCell(OutputShareBar, OutputValueText, "—", 0, "RuVioletBrush", false);
            PaintShareCell(CacheReadShareBar, CacheReadValueText, "—", 0, "RuInfoBrush", false);
            PaintShareCell(CacheWriteShareBar, CacheWriteValueText, "—", 0, "RuPinkBrush", false);

            IReadOnlyList<SessionUsageRow>? rows;
            try
            {
                rows = await api.ListSessionUsageAsync(sessionId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                GuiDiagnosticWriter.AppendTraceLine($"Session usage read failed: {exception.Message}");
                rows = null;
            }

            Render(rows);
        }

        private void Render(IReadOnlyList<SessionUsageRow>? rows)
        {
            var summary = SessionUsagePolicy.Summarize(rows ?? Array.Empty<SessionUsageRow>());
            int count = rows?.Count ?? 0;

            CoverageText.Text = rows is null
                ? "unavailable"
                : summary.ReportedRuns == count
                    ? count == 0 ? "no runs" : $"all {count} reported"
                    : $"{summary.ReportedRuns} of {count} reported";

            BilledValueText.Text = ModelContextSpecs.FormatTokens(summary.BilledTokens);
            PeakValueText.Text = ModelContextSpecs.FormatTokens(summary.PeakTotal);
            ElapsedValueText.Text = FormatElapsed(rows);

            InputValueText.Text = ModelContextSpecs.FormatTokens(summary.TotalInput);
            OutputValueText.Text = ModelContextSpecs.FormatTokens(summary.TotalOutput);

            // Reads and writes are separate figures, not one cache figure. Adding them
            // hid the one thing the split is for: a session that mostly re-read a warm
            // cache looks identical to one that mostly paid for fresh input.
            PaintShareCell(
                InputShareBar, InputValueText,
                ModelContextSpecs.FormatTokens(summary.TotalInput),
                summary.InputShare, "RuAccentBrush", summary.HasAny);
            PaintShareCell(
                OutputShareBar, OutputValueText,
                ModelContextSpecs.FormatTokens(summary.TotalOutput),
                summary.OutputShare, "RuVioletBrush", summary.HasAny);
            PaintShareCell(
                CacheReadShareBar, CacheReadValueText,
                ModelContextSpecs.FormatTokens(summary.CacheRead),
                summary.CacheReadShare, "RuInfoBrush", summary.CacheRead > 0);
            PaintShareCell(
                CacheWriteShareBar, CacheWriteValueText,
                ModelContextSpecs.FormatTokens(summary.CacheWrite),
                summary.CacheWriteShare, "RuPinkBrush", summary.CacheWrite > 0);

            RunsHeaderText.Text = count switch
            {
                0 => "Runs",
                1 => "1 run",
                _ => $"{count} runs",
            };
            RunsHeaderText.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;

            RowsPanel.Children.Clear();
            // Newest first: the run the user just watched is the one they opened the
            // panel for, and counting down from it is how a log reads.
            for (int index = count - 1; index >= 0; index--)
            {
                RowsPanel.Children.Add(BuildRow(count - index, rows![index], summary));
            }

            EmptyText.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = rows is null
                ? "The session's runs could not be read. The host answered, but not with usage."
                : count == 0
                    ? "This session has no runs yet. Reports arrive with the first completed step."
                    : "No run in this session has reported usage yet.";

            FooterText.Text = count < SessionUsagePolicy.MaxReportedRuns && count > 0
                ? $"Whole session · {count} run{(count == 1 ? string.Empty : "s")}"
                : $"Newest {SessionUsagePolicy.MaxReportedRuns} runs";

            PlayArrival();
        }

        /// <summary>
        /// Wall time from the first run's start to the last run's end.
        /// </summary>
        /// <remarks>
        /// Every terminal run has to carry both ends. Summing durations instead would
        /// answer a different question — how long the agent spent working, not how long
        /// the session took — and picking the earliest start and latest end from a
        /// partial set would understate the span without saying so.
        /// </remarks>
        private static string FormatElapsed(IReadOnlyList<SessionUsageRow>? rows)
        {
            if (rows is null || rows.Count == 0)
            {
                return "—";
            }

            DateTimeOffset? start = null;
            DateTimeOffset? end = null;
            foreach (var row in rows)
            {
                if (!row.IsTerminal || row.CreatedAt is not { } rowStart || row.TerminalAt is not { } rowEnd)
                {
                    return "—";
                }

                if (start is null || rowStart < start)
                {
                    start = rowStart;
                }

                if (end is null || rowEnd > end)
                {
                    end = rowEnd;
                }
            }

            return start is { } first && end is { } last && last >= first
                ? SessionTimeFormatter.FormatDuration(last - first)
                : "—";
        }

        /// <summary>
        /// Paints one part of the split: its figure, and a bar sized against the whole
        /// measured total so the four lengths are directly comparable and their sum is
        /// the width of the section.
        /// </summary>
        /// <remarks>
        /// The bar is two star columns rather than a computed pixel width: a cell's
        /// width is not known until layout runs, and asking for it here would mean a
        /// second pass over the panel every time it is opened. A part that measured
        /// nothing gets a zero-width column and so draws nothing at all — never a
        /// sliver, which would be a figure the panel cannot correct.
        /// </remarks>
        private static void PaintShareCell(
            Grid bar,
            TextBlock value,
            string figure,
            double share,
            string fillKey,
            bool measured)
        {
            value.Text = measured ? figure : "—";
            value.Foreground = ThemeBrush(measured ? "RuInkBrush" : "RuMutedBrush");

            bar.Children.Clear();
            bar.ColumnDefinitions.Clear();
            if (share <= 0)
            {
                return;
            }

            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - share, GridUnitType.Star) });

            var segment = new Border { Background = ThemeBrush(fillKey) };
            Grid.SetColumn(segment, 0);
            bar.Children.Add(segment);
        }

        /// <summary>
        /// One run: when it ran, how it ended, how long it took, what the provider
        /// counted, and its share of the session's billed total.
        /// </summary>
        /// <remarks>
        /// Two lines, because one line cannot hold all of it at a legible size. The
        /// entry announces the whole thing, so a screen reader does not have to stitch
        /// the lines together.
        /// </remarks>
        private static FrameworkElement BuildRow(int position, SessionUsageRow row, SessionUsageSummary summary)
        {
            var ink = ThemeBrush("RuInkBrush");
            var muted = ThemeBrush("RuMutedBrush");
            var mono = (FontFamily)Application.Current.Resources["RuMonoFontFamily"];
            var ui = (FontFamily)Application.Current.Resources["RuUiFontFamily"];

            var place = new TextBlock
            {
                Text = $"#{position}",
                FontFamily = mono,
                FontSize = 11,
                Foreground = muted,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0),
            };

            int measured = row.Usage?.ResolvedTotal ?? 0;
            double share = SessionUsagePolicy.ShareOfPeak(measured, summary.PeakTotal);
            string shareText = measured > 0 && summary.HasAny
                ? summary.ShareOfBilled(measured).ToString("0", CultureInfo.InvariantCulture) + "%"
                : string.Empty;

            string started = row.CreatedAt is { } created
                ? created.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
            string took = SessionTimeFormatter.FormatDuration(row.Duration);

            var head = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
            var facts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (started.Length > 0)
            {
                facts.Children.Add(Fact(started, mono, muted));
            }

            if (!row.HasUsage || row.DidNotSucceed)
            {
                // The host's own word, coloured by what it means. A run that reported no
                // usage has usually not failed, and the old wording made every quiet run
                // look like a broken one.
                facts.Children.Add(Fact(row.Outcome, ui, ThemeBrush(OutcomeBrush(row)), semiBold: true));
            }

            if (took.Length > 0)
            {
                facts.Children.Add(Fact(took, mono, muted));
            }

            if (facts.Children.Count > 0)
            {
                head.Children.Add(facts);
            }

            // Thinner than the split bars above, in the same colours: the two carry the
            // same kind of reading on a different scale, and a table column that weighs
            // as much as the composition it sits under reads as a second composition.
            const double TrackWidth = 56;
            var track = new Grid { Width = TrackWidth, Height = 3, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0) };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            track.Children.Add(new Border
            {
                Background = ThemeBrush("RuCanvasBrush"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            });

            if (share > 0)
            {
                var fill = new Border
                {
                    Background = ThemeBrush("RuAccentBrush"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Width = TrackWidth * share / 100.0,
                };
                Grid.SetColumn(fill, 0);
                track.Children.Add(fill);
            }

            var headGrid = new Grid { ColumnSpacing = 10 };
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26, GridUnitType.Pixel) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TrackWidth, GridUnitType.Pixel) });
            Grid.SetColumn(place, 0);
            Grid.SetColumn(head, 1);
            if (shareText.Length > 0)
            {
                var shareBlock = new TextBlock
                {
                    Text = shareText,
                    FontFamily = mono,
                    FontSize = 11,
                    Foreground = muted,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                Grid.SetColumn(shareBlock, 2);
                headGrid.Children.Add(shareBlock);
            }

            Grid.SetColumn(track, 3);
            headGrid.Children.Add(place);
            headGrid.Children.Add(head);
            headGrid.Children.Add(track);

            var body = new StackPanel();
            if (row.HasUsage)
            {
                body.Children.Add(new TextBlock
                {
                    Text = row.Detail,
                    FontFamily = mono,
                    FontSize = 11,
                    Foreground = ink,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(36, 2, TrackWidth + 10, 0),
                });
            }

            var column = new StackPanel { Spacing = 2 };
            column.Children.Add(headGrid);
            column.Children.Add(body);

            // The rule belongs to the row, not to a container: a ledger whose last line
            // is underlined reads as an unfinished table.
            var line = new Border
            {
                BorderBrush = ThemeBrush("RuLineBrush"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 8, 0, 8),
                Child = column,
            };

            AutomationProperties.SetName(
                line,
                $"Run {position}, {row.Outcome}"
                + (row.CreatedAt is { } at ? $", started {at.ToLocalTime():HH:mm}" : string.Empty)
                + (row.Duration is { } duration ? $", took {SessionTimeFormatter.FormatDuration(duration)}" : string.Empty)
                + (row.HasUsage ? $", {row.Detail}" : string.Empty)
                + (shareText.Length > 0 ? $", {shareText} of the session" : string.Empty));
            return line;
        }

        private static TextBlock Fact(string text, FontFamily family, Brush ink, bool semiBold = false) =>
            new()
            {
                Text = text,
                FontFamily = family,
                FontSize = 11,
                FontWeight = semiBold
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = ink,
            };

        /// <summary>The semantic colour for how a run ended.</summary>
        private static string OutcomeBrush(SessionUsageRow row) => row.Status switch
        {
            "completed" => "RuSuccessBrush",
            "failed" or "reconciliation_required" => "RuDangerBrush",
            "interrupted" or "cancelled" => "RuWarningBrush",
            _ when row.IsWaiting => "RuInfoBrush",
            _ => "RuMutedBrush",
        };

        /// <summary>
        /// Fades the body in once the read lands. One authored moment, and it is
        /// skipped when the system has animations turned off — the numbers arrive
        /// either way.
        /// </summary>
        private void PlayArrival()
        {
            if (RowsPanel.Children.Count == 0)
            {
                return;
            }

            var transform = new TranslateTransform();
            BodyPanel.RenderTransform = new TransformGroup { Children = { transform } };

            if (!UiTiming.AnimationsEnabled)
            {
                return;
            }

            BodyPanel.Opacity = 0;
            var fade = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(LedgerArrival),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, BodyPanel);
            Storyboard.SetTargetProperty(fade, "Opacity");

            var rise = new DoubleAnimation
            {
                From = 6.0,
                To = 0.0,
                Duration = new Duration(LedgerArrival),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(rise, transform);
            Storyboard.SetTargetProperty(rise, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Children.Add(rise);
            storyboard.Begin();
        }

        private static Brush ThemeBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
