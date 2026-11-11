using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Controls;
using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Windows.Storage.Streams;
using System.IO;

namespace RuApp.Views
{
    public sealed partial class ChatView
    {
        private readonly List<Button> _reasoningRows = new();
        private readonly List<TextBlock> _reasoningLabels = new();
        private readonly List<string> _reasoningEfforts = new();
        private OptionListKeyboard _reasoningKeyboard = new(0, _ => { }, () => { });
        private Border? _reasoningPill;
        private Grid? _reasoningSegments;
        private TextBlock? _reasoningDescription;
        private bool _reasoningPillPlaced;
        /// <summary>The composer's provider/model and reasoning-effort state.</summary>
        public ChatModelPickerViewModel ModelSelection { get; }

        public Task LoadChatModelsAsync() => ModelSelection.LoadAsync();

        /// <summary>
        /// The catalog for the selected backend, or null when none has arrived; the
        /// session usage panel prices runs from it.
        /// </summary>
        public ProviderCatalogState? ProviderCatalogState => ModelSelection.SelectedCatalog;

        private void AttachModelSelection()
        {
            if (_apiService is null || _providerCatalogController is null || _groupedCatalogs is null) return;
            ModelSelection.Attach(
                new ApiChatModelBackend(_apiService),
                new ProviderCatalogSession(_providerCatalogController),
                new GroupedCatalogSession(_groupedCatalogs));
        }

        private void OnModelSelectionPickerReady(ProviderModelPickerProjection projection) => ModelPicker.Apply(projection);

        private void OnModelSelectionChanged() => UpdateContextUsage();

        private void OnChatModelFlyoutOpened(object sender, object e)
        {
            AttachChatFlyoutEscape(ChatModelFlyout);
            UiMotion.Enter(ModelPicker);
            ModelPicker.FocusSearch();
            ModelSelection.OnPickerOpened();
        }

        private void OnChatModelFlyoutClosed(object sender, object e)
        {
            OnChatOwnedFlyoutClosed(ChatModelFlyout);
            ModelSelection.OnPickerClosed();
        }

        private void OnModelPickerSearchTextChanged(object? sender, string searchText) =>
            ModelSelection.SetSearchText(searchText);

        private void OnModelPickerModelSelected(object? sender, ProviderModelSelectionEventArgs e)
        {
            ChatModelFlyout.Hide();
            _ = ModelSelection.SelectModelAsync(e.Model, e.Backend);
        }

        /// <summary>
        /// Escape, or any other way the list asked to be left without a choice. The
        /// flyout belongs to this view (it sits on the toolbar button), so it hides here;
        /// light dismiss alone is unreliable while a text field holds the focus.
        /// </summary>
        private void OnModelPickerDismissRequested(object? sender, EventArgs e) =>
            ChatModelFlyout.Hide();

        /// <summary>
        /// Builds the reasoning panel: one segmented choice and a sentence for the segment
        /// under the cursor.
        /// </summary>
        /// <remarks>
        /// A pill marks the segment the pointer or the keys are on and glides between
        /// segments; the sentence below fades when it changes. Clicking or pressing Enter
        /// commits the marked effort and closes the panel, and leaving without a commit
        /// changes nothing. The sentences come from <see cref="ReasoningEffortDescriptions"/>
        /// and say what a setting asks of the model, never how a provider behaves. A model
        /// that offers only its default shows a single dimmed segment and says so.
        ///
        /// The container takes the focus and the segments are not tab stops, so Tab leaves
        /// the flyout rather than walking the buttons to get out of it.
        /// </remarks>
        private void OnChatReasoningFlyoutOpened(object sender, object e)
        {
            AttachChatFlyoutEscape(ChatReasoningFlyout);
            ReasoningOptionsContainer.Children.Clear();
            _reasoningRows.Clear();
            _reasoningLabels.Clear();
            _reasoningEfforts.Clear();
            _reasoningPillPlaced = false;
            var options = ModelSelection.ReasoningOptions();
            bool adjustable = options.Count > 1;

            var resources = Application.Current.Resources;
            var font = (FontFamily)resources["RuUiFontFamily"];
            var muted = (Brush)resources["RuMutedBrush"];
            var line = (Brush)resources["RuLineBrush"];
            var rowStyle = (Style)resources["RuOptionRowStyle"];

            ReasoningOptionsContainer.Children.Add(new TextBlock
            {
                Text = $"Reasoning effort · {ModelSelection.ModelLabel}",
                FontFamily = font,
                FontSize = 12,
                Foreground = muted,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 2, 4, 8),
            });

            var segments = new Grid();
            var pill = new Border
            {
                Background = (Brush)resources["RuAccentSoftBrush"],
                CornerRadius = Services.UiCornerRadius.Control,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsHitTestVisible = false,
            };
            Grid.SetColumnSpan(pill, Math.Max(1, options.Count));
            segments.Children.Add(pill);
            _reasoningPill = pill;
            _reasoningSegments = segments;

            int selectedIndex = 0;
            foreach (var option in options)
            {
                bool isSelected = string.Equals(option.Value, ModelSelection.SelectedEffort, StringComparison.OrdinalIgnoreCase);
                string label = option.Label ?? option.Value;
                int index = _reasoningRows.Count;
                if (isSelected) selectedIndex = index;

                segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var text = new TextBlock
                {
                    Text = CapitalizeEffort(label),
                    FontFamily = font,
                    FontSize = 12.5,
                    Foreground = muted,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var btn = new Button
                {
                    Style = rowStyle,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Padding = new Thickness(0),
                    MinHeight = 30,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Content = text,
                    Tag = index,
                };
                AutomationProperties.SetName(btn, $"Reasoning effort: {label}");
                btn.PointerEntered += OnReasoningRowPointerEntered;
                btn.Click += OnReasoningOptionClicked;
                Grid.SetColumn(btn, index);
                segments.Children.Add(btn);
                _reasoningRows.Add(btn);
                _reasoningLabels.Add(text);
                _reasoningEfforts.Add(option.Value);
            }

            segments.SizeChanged += OnReasoningSegmentsSizeChanged;
            ReasoningOptionsContainer.Children.Add(new Border
            {
                Background = (Brush)resources["RuCanvasBrush"],
                BorderBrush = line,
                BorderThickness = new Thickness(1),
                CornerRadius = Services.UiCornerRadius.Control,
                Padding = new Thickness(3),
                Opacity = adjustable ? 1 : 0.55,
                Child = segments,
            });

            _reasoningDescription = new TextBlock
            {
                FontFamily = font,
                FontSize = 12,
                LineHeight = 17,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 34,
                Margin = new Thickness(4, 10, 4, 2),
            };
            ReasoningOptionsContainer.Children.Add(_reasoningDescription);

            _reasoningKeyboard = new OptionListKeyboard(
                _reasoningRows.Count,
                ActivateReasoningRow,
                () => ChatReasoningFlyout.Hide());
            _reasoningKeyboard.CursorChanged += OnReasoningCursorChanged;
            _reasoningKeyboard.MoveCursor(selectedIndex);

            UiMotion.Enter(ReasoningOptionsContainer);
            ReasoningOptionsContainer.Focus(FocusState.Programmatic);
        }

        private static string CapitalizeEffort(string label) =>
            string.IsNullOrEmpty(label) ? label : char.ToUpperInvariant(label[0]) + label[1..];

        private void OnChatReasoningFlyoutClosed(object sender, object e)
        {
            if (_reasoningSegments is not null) _reasoningSegments.SizeChanged -= OnReasoningSegmentsSizeChanged;
            OnChatOwnedFlyoutClosed(ChatReasoningFlyout);
        }

        private void OnContextWindowFlyoutOpened(object sender, object e) =>
            AttachChatFlyoutEscape(ContextWindowFlyout);

        private void OnContextWindowFlyoutClosed(object sender, object e) =>
            OnChatOwnedFlyoutClosed(ContextWindowFlyout);

        /// <summary>
        /// The pointer is the cursor, exactly as in the model picker: one marked segment,
        /// so "what will Enter pick" has a single answer whichever device the user reached
        /// for.
        /// </summary>
        private void OnReasoningRowPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button { Tag: int index })
            {
                _reasoningKeyboard.MoveCursor(index, show: false);
            }
        }

        // The pill is as wide as one segment; it is sized, and put under the cursor, once
        // the segments have a width.
        private void OnReasoningSegmentsSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_reasoningPill is null || _reasoningRows.Count == 0 || e.NewSize.Width <= 0) return;
            _reasoningPill.Width = e.NewSize.Width / _reasoningRows.Count;
            _reasoningPill.Height = e.NewSize.Height;
            MoveReasoningPill(Math.Max(0, _reasoningKeyboard.Cursor), animate: false);
            _reasoningPillPlaced = true;
        }

        private void MoveReasoningPill(int index, bool animate)
        {
            if (_reasoningPill is null || _reasoningSegments is null || _reasoningRows.Count == 0) return;
            double width = _reasoningSegments.ActualWidth / _reasoningRows.Count;
            UiMotion.SlideX(_reasoningPill, index * width, animate);
        }

        private void OnReasoningCursorChanged(int index)
        {
            var resources = Application.Current.Resources;
            var ink = (Brush)resources["RuInkBrush"];
            var muted = (Brush)resources["RuMutedBrush"];
            for (int i = 0; i < _reasoningLabels.Count; i++)
            {
                _reasoningLabels[i].Foreground = i == index ? ink : muted;
                _reasoningLabels[i].FontWeight = i == index
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
            }

            MoveReasoningPill(index, animate: _reasoningPillPlaced);

            if (_reasoningDescription is not null && index >= 0 && index < _reasoningEfforts.Count)
            {
                string text = _reasoningEfforts.Count > 1
                    ? ReasoningEffortDescriptions.For(_reasoningEfforts[index])
                    : ReasoningEffortDescriptions.NotAdjustable;
                if (!string.Equals(_reasoningDescription.Text, text, StringComparison.Ordinal))
                {
                    _reasoningDescription.Text = text;
                    if (_reasoningPillPlaced)
                    {
                        // The sentence follows the cursor: it fades in rather than swapping.
                        _reasoningDescription.Opacity = 0;
                        UiMotion.FadeOpacity(_reasoningDescription, 1);
                    }
                }
            }
        }
        private void OnReasoningContainerKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // The segments run left to right, so Left and Right walk them like Up and Down.
            if (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)
            {
                _reasoningKeyboard.MoveCursorBy(e.Key == Windows.System.VirtualKey.Left ? -1 : 1);
                e.Handled = true;
                return;
            }

            // No text field holds the focus here, so Space is free to commit.
            if (_reasoningKeyboard.HandleKey(e.Key, spaceActivates: true))
            {
                e.Handled = true;
            }
        }

        private void ActivateReasoningRow(int index) => _ = CommitReasoningEffortAsync(index);

        private async void OnReasoningOptionClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: int index })
            {
                await CommitReasoningEffortAsync(index);
            }
        }

        private async Task CommitReasoningEffortAsync(int index)
        {
            try
            {
                if (index < 0 || index >= _reasoningEfforts.Count)
                {
                    return;
                }

                ChatReasoningFlyout.Hide();
                await ModelSelection.ApplyEffortAsync(_reasoningEfforts[index]);
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Reasoning effort selection failed: {ex.Message}");
            }
        }

    }
}
