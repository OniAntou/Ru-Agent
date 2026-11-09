using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RuApp.Models;
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

        public TodoChecklistControl()
        {
            Content = _rows;
        }

        /// <summary>
        /// Draws the list. Passing null or an empty list clears it.
        /// </summary>
        public void Render(IReadOnlyList<TodoItemModel>? todos)
        {
            _rows.Children.Clear();
            if (todos is null || todos.Count == 0)
            {
                return;
            }

            int ordinal = 0;
            foreach (TodoItemModel todo in todos)
            {
                _rows.Children.Add(BuildRow(todo, ++ordinal));
            }
        }

        private static Grid BuildRow(TodoItemModel todo, int ordinal)
        {
            var row = new Grid
            {
                ColumnSpacing = 8,
                MinHeight = 28,
                Padding = new Thickness(4, 2, 8, 2),
                CornerRadius = RuApp.Services.UiCornerRadius.Control,
                // The active row carries the only tint; completed and pending rows stay
                // on the shelf's own surface.
                Background = todo.IsInProgress ? Brush("RuAccentSoftBrush", FallbackInsetBrush) : null,
            };

            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            double activitySize = RuApp.Services.TranscriptTypography.Instance.ActivityFontSize;

            var number = new TextBlock
            {
                Text = ordinal.ToString("00", CultureInfo.InvariantCulture),
                FontFamily = MonoFont,
                // One step under the label; the ordinal is a reading aid, not content.
                FontSize = Math.Max(9, activitySize - 3.5),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = todo.IsInProgress
                    ? Brush("RuAccentBrush", FallbackAccentBrush)
                    : Brush("RuMutedBrush", FallbackMutedBrush),
                Opacity = todo.IsCompleted ? 0.65 : 1,
            };

            // The active mark is a 5px dot, the pending mark a 6px ring and the completed
            // mark a small check: glyph-sized circles read as buttons in a dense list.
            FrameworkElement mark;
            if (todo.IsInProgress)
            {
                mark = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = Brush("RuAccentBrush", FallbackAccentBrush),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else if (todo.IsCompleted)
            {
                mark = new FontIcon
                {
                    Glyph = todo.MarkGlyph,
                    FontSize = Math.Max(10, activitySize - 3),
                    Foreground = Brush("RuSuccessBrush", FallbackSuccessBrush),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            else
            {
                mark = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Stroke = Brush("RuMutedBrush", FallbackMutedBrush),
                    StrokeThickness = 1,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            var text = new TextBlock
            {
                Text = todo.Content,
                FontFamily = UiFont,
                FontSize = Math.Max(11, activitySize - 1),
                TextWrapping = TextWrapping.Wrap,
                // The active row is the only emphasised one, which is what the
                // single-active rule buys the eye.
                FontWeight = todo.IsInProgress ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = todo.IsCompleted
                    ? Brush("RuMutedBrush", FallbackMutedBrush)
                    : Brush("RuInkBrush", FallbackInkBrush),
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(number, 0);
            Grid.SetColumn(mark, 1);
            Grid.SetColumn(text, 2);
            row.Children.Add(number);
            row.Children.Add(mark);
            row.Children.Add(text);

            AutomationProperties.SetName(row, todo.AccessibleName);
            return row;
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
    }
}
