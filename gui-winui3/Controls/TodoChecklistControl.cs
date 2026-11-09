using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using RuApp.Models;
using RuApp.Services;
using Windows.Foundation;
using Windows.UI;

namespace RuApp.Controls
{
    /// <summary>
    /// The agent's task list, drawn as a read-only checklist.
    /// </summary>
    /// <remarks>
    /// Built in code rather than markup, following the other controls in this folder. It
    /// deliberately offers nothing to click: the agent plans and the user watches, so any
    /// affordance here would be a promise the product does not keep.
    ///
    /// The three states are distinguished by mark, weight and colour together, not by
    /// colour alone, so the list still reads for someone who cannot tell the accent from
    /// the muted tone. The active row also carries a quiet surface tint and an accent
    /// ordinal; there is no separate "NOW" label to read on every refresh.
    ///
    /// Rows are reconciled by content: an advancing plan updates the existing elements
    /// in place so the tint and marks can cross-fade instead of snapping through a
    /// rebuild. A genuinely different plan rebuilds its rows once.
    /// </remarks>
    public sealed partial class TodoChecklistControl : UserControl
    {
        private static readonly FontFamily UiFont = new("ms-appx:///Assets/Fonts/Inter-Variable.ttf#Inter, Segoe UI Variable, Segoe UI, Segoe UI Emoji");

        private static readonly SolidColorBrush FallbackInkBrush =
            new(Color.FromArgb(255, 241, 245, 249));
        private static readonly SolidColorBrush FallbackMutedBrush =
            new(Color.FromArgb(255, 148, 163, 184));
        private static readonly SolidColorBrush FallbackAccentBrush =
            new(Color.FromArgb(255, 226, 232, 240));
        private static readonly SolidColorBrush FallbackSuccessBrush =
            new(Color.FromArgb(255, 52, 211, 153));
        private static readonly SolidColorBrush FallbackInsetBrush =
            new(Color.FromArgb(255, 46, 46, 46));

        private readonly StackPanel _rows = new() { Spacing = 2 };
        private readonly List<TodoRowView> _rowViews = new();
        private string[] _renderedContents = Array.Empty<string>();

        public TodoChecklistControl()
        {
            Content = _rows;
        }

        /// <summary>
        /// Draws the list. Passing null or an empty list clears it.
        /// </summary>
        public void Render(IReadOnlyList<TodoItemModel>? todos)
        {
            var items = todos ?? Array.Empty<TodoItemModel>();
            if (items.Count == 0)
            {
                ClearRows();
                return;
            }

            if (items.Count != _rowViews.Count || !ContentsMatch(items))
            {
                BuildRows(items);
                return;
            }

            for (int index = 0; index < items.Count; index++)
            {
                ApplyState(_rowViews[index], items[index], ordinal: index + 1, animate: true);
            }
        }

        private bool ContentsMatch(IReadOnlyList<TodoItemModel> items)
        {
            for (int index = 0; index < items.Count; index++)
            {
                if (!string.Equals(_renderedContents[index], items[index].Content, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private void BuildRows(IReadOnlyList<TodoItemModel> items)
        {
            ClearRows();
            var contents = new string[items.Count];
            for (int index = 0; index < items.Count; index++)
            {
                TodoItemModel todo = items[index];
                TodoRowView view = BuildRow(todo, ordinal: index + 1);
                ApplyState(view, todo, ordinal: index + 1, animate: false);
                _rowViews.Add(view);
                _rows.Children.Add(view.Root);
                contents[index] = todo.Content;
            }

            _renderedContents = contents;
        }

        private void ClearRows()
        {
            foreach (TodoRowView view in _rowViews)
            {
                view.DotPop?.Stop();
            }

            _rows.Children.Clear();
            _rowViews.Clear();
            _renderedContents = Array.Empty<string>();
        }

        private static TodoRowView BuildRow(TodoItemModel todo, int ordinal)
        {
            var view = new TodoRowView();
            var row = new Grid
            {
                ColumnSpacing = 8,
                MinHeight = 28,
                Padding = new Thickness(4, 2, 8, 2),
                CornerRadius = UiCornerRadius.Control,
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            double activitySize = TranscriptTypography.Instance.ActivityFontSize;

            var number = new TextBlock
            {
                Text = ordinal.ToString("00", CultureInfo.InvariantCulture),
                FontFamily = MonoFont,
                // One step under the label; the ordinal is a reading aid, not content.
                FontSize = Math.Max(9, activitySize - 3.5),
                VerticalAlignment = VerticalAlignment.Center,
            };

            // A drawn mark, not a glyph-sized circle: the active dot stays 7px, the
            // pending ring 8px and the completed check one step under the label.
            var mark = new Grid
            {
                Width = 16,
                Height = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brush("RuAccentBrush", FallbackAccentBrush),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
            };
            var ring = new Ellipse
            {
                Width = 8,
                Height = 8,
                Stroke = Brush("RuMutedBrush", FallbackMutedBrush),
                StrokeThickness = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
            };
            var check = new FontIcon
            {
                Glyph = TodoItemModel.CompletedGlyph,
                FontSize = Math.Max(10, activitySize - 3),
                Foreground = Brush("RuSuccessBrush", FallbackSuccessBrush),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
            };
            mark.Children.Add(dot);
            mark.Children.Add(ring);
            mark.Children.Add(check);

            var label = new TextBlock
            {
                Text = todo.Content,
                FontFamily = UiFont,
                FontSize = Math.Max(11, activitySize - 1),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(number, 0);
            Grid.SetColumn(mark, 1);
            Grid.SetColumn(label, 2);
            row.Children.Add(number);
            row.Children.Add(mark);
            row.Children.Add(label);

            AutomationProperties.SetName(row, todo.AccessibleName);
            view.Root = row;
            view.Number = number;
            view.Dot = dot;
            view.Ring = ring;
            view.Check = check;
            view.Label = label;
            return view;
        }

        private static void ApplyState(TodoRowView view, TodoItemModel todo, int ordinal, bool animate)
        {
            Brush accent = Brush("RuAccentBrush", FallbackAccentBrush);
            Brush muted = Brush("RuMutedBrush", FallbackMutedBrush);
            Brush ink = Brush("RuInkBrush", FallbackInkBrush);
            Brush success = Brush("RuSuccessBrush", FallbackSuccessBrush);
            Brush tint = Brush("RuAccentSoftBrush", FallbackInsetBrush);

            bool active = todo.IsInProgress;
            bool done = todo.IsCompleted;

            string ordinalText = ordinal.ToString("00", CultureInfo.InvariantCulture);
            if (!string.Equals(view.Number.Text, ordinalText, StringComparison.Ordinal))
            {
                view.Number.Text = ordinalText;
            }

            SetForeground(view.Number, active ? accent : muted, animate);
            view.Number.Opacity = done ? 0.65 : 1;
            SetForeground(view.Label, done ? muted : ink, animate);
            view.Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            SetBackground(view.Root, active ? tint : null, animate);

            SetOpacity(view.Dot, active ? 1 : 0, animate);
            SetOpacity(view.Ring, !active && !done ? 1 : 0, animate);
            SetOpacity(view.Check, done ? 1 : 0, animate);

            if (active && !view.WasActive && animate)
            {
                PopDot(view);
            }
            else if (!active)
            {
                ResetDot(view);
            }

            view.WasActive = active;
        }

        private static void SetForeground(TextBlock element, Brush target, bool animate)
        {
            if (animate)
            {
                UiMotion.FadeBrush(element, TextBlock.ForegroundProperty, target);
            }
            else
            {
                element.Foreground = target;
            }
        }

        private static void SetBackground(FrameworkElement element, Brush? target, bool animate)
        {
            if (animate)
            {
                UiMotion.FadeBrush(element, Panel.BackgroundProperty, target);
            }
            else
            {
                element.SetValue(Panel.BackgroundProperty, target);
            }
        }

        private static void SetOpacity(FrameworkElement element, double target, bool animate)
        {
            if (Math.Abs(element.Opacity - target) < 0.01)
            {
                return;
            }

            if (animate)
            {
                UiMotion.FadeOpacity(element, target);
            }
            else
            {
                element.Opacity = target;
            }
        }

        private static void PopDot(TodoRowView view)
        {
            view.DotPop?.Stop();
            view.DotPop = null;
            if (!UiTiming.AnimationsEnabled)
            {
                return;
            }

            var scale = new ScaleTransform { ScaleX = 0.8, ScaleY = 0.8 };
            view.Dot.RenderTransformOrigin = new Point(0.5, 0.5);
            view.Dot.RenderTransform = scale;
            try
            {
                var growX = new DoubleAnimation
                {
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(120),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                var growY = new DoubleAnimation
                {
                    To = 1,
                    Duration = TimeSpan.FromMilliseconds(120),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(growX, scale);
                Storyboard.SetTargetProperty(growX, nameof(ScaleTransform.ScaleX));
                Storyboard.SetTarget(growY, scale);
                Storyboard.SetTargetProperty(growY, nameof(ScaleTransform.ScaleY));

                var storyboard = new Storyboard();
                storyboard.Children.Add(growX);
                storyboard.Children.Add(growY);
                storyboard.Completed += (_, _) =>
                {
                    scale.ScaleX = 1;
                    scale.ScaleY = 1;
                };
                view.DotPop = storyboard;
                storyboard.Begin();
            }
            catch (Exception)
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
        }

        private static void ResetDot(TodoRowView view)
        {
            view.DotPop?.Stop();
            view.DotPop = null;
            if (view.Dot.RenderTransform is ScaleTransform scale)
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
        }

        /// <summary>
        /// A themed brush, or the literal fallback when the app resources are not loaded.
        /// </summary>
        /// <remarks>
        /// The fallbacks keep this control renderable in a test host, where
        /// Application.Current has no resources. They are never the intended look.
        /// </remarks>
        private static Brush Brush(string resourceKey, Brush fallback)
        {
            if (Application.Current?.Resources is { } resources
                && resources.TryGetValue(resourceKey, out object? value)
                && value is Brush brush)
            {
                return brush;
            }

            return fallback;
        }

        /// <summary>The app's mono stack, or a system fallback when resources are not loaded.</summary>
        private static FontFamily MonoFont
        {
            get
            {
                if (Application.Current?.Resources is { } resources
                    && resources.TryGetValue("RuMonoFontFamily", out object? value)
                    && value is FontFamily font)
                {
                    return font;
                }

                return new FontFamily("Consolas");
            }
        }

        private sealed class TodoRowView
        {
            public Grid Root { get; set; } = null!;
            public TextBlock Number { get; set; } = null!;
            public Ellipse Dot { get; set; } = null!;
            public Ellipse Ring { get; set; } = null!;
            public FontIcon Check { get; set; } = null!;
            public TextBlock Label { get; set; } = null!;
            public bool WasActive { get; set; }
            public Storyboard? DotPop { get; set; }
        }
    }
}
