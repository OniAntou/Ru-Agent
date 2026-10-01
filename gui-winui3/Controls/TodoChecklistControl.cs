using System;
using System.Collections.Generic;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    /// the muted tone.
    /// </remarks>
    public sealed partial class TodoChecklistControl : UserControl
    {
        private static readonly FontFamily UiFont = new("ms-appx:///Assets/Fonts/Inter-Variable.ttf#Inter, Segoe UI Variable, Segoe UI, Segoe UI Emoji");

        private static readonly SolidColorBrush FallbackInkBrush =
            new(Color.FromArgb(255, 241, 245, 249));
        private static readonly SolidColorBrush FallbackMutedBrush =
            new(Color.FromArgb(255, 148, 163, 184));
        private static readonly SolidColorBrush FallbackAccentBrush =
            new(Color.FromArgb(255, 129, 140, 248));

        private readonly StackPanel _rows = new() { Spacing = 9 };

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

            foreach (TodoItemModel todo in todos)
            {
                _rows.Children.Add(BuildRow(todo));
            }
        }

        private static Grid BuildRow(TodoItemModel todo)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var mark = new FontIcon
            {
                Glyph = todo.MarkGlyph,
                // Scaled with the text so the mark never drifts out of proportion with the
                // line it belongs to.
                FontSize = RuApp.Services.TranscriptTypography.Instance.ActivityFontSize - 1.5,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = todo.IsInProgress ? Brush("RuAccentBrush", FallbackAccentBrush)
                    : todo.IsCompleted ? Brush("AppSuccessBrush", FallbackMutedBrush)
                    : Brush("RuMutedBrush", FallbackMutedBrush),
            };

            var text = new TextBlock
            {
                Text = todo.Content,
                FontFamily = UiFont,
                // One step below the conversation, the same relationship the activity line
                // has to it. A fixed size ignored the reader's own choice.
                FontSize = RuApp.Services.TranscriptTypography.Instance.ActivityFontSize,
                TextWrapping = TextWrapping.Wrap,
                // The active row is the only emphasised one, which is what the
                // single-active rule buys the eye.
                FontWeight = todo.IsInProgress ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = todo.IsCompleted
                    ? Brush("RuMutedBrush", FallbackMutedBrush)
                    : Brush("RuInkBrush", FallbackInkBrush),
                TextDecorations = todo.IsCompleted
                    ? Windows.UI.Text.TextDecorations.Strikethrough
                    : Windows.UI.Text.TextDecorations.None,
            };

            Grid.SetColumn(mark, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(mark);
            row.Children.Add(text);

            AutomationProperties.SetName(row, todo.AccessibleName);
            return row;
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
