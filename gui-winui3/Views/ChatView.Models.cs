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
        /// <summary>
        /// Width the reasoning rows reserve for the margin beside the selection rule, so
        /// every label starts on the same line whether or not the rule is drawn.
        /// </summary>
        /// <remarks>
        /// The same 11px the model picker reserves, and for the same reason: a rule that
        /// only exists on the selected row must not push that row's label out of line
        /// with the other three.
        /// </remarks>
        private const double ReasoningSelectionColumnWidth = 11;

        private readonly List<Button> _reasoningRows = new();
        private readonly List<string> _reasoningEfforts = new();
        private OptionListKeyboard _reasoningKeyboard = new(0, _ => { }, () => { });
        private Brush? _reasoningCursorBrush;
        private Brush? _reasoningRestBrush;

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
            ModelPicker.FocusSearch();
            ModelSelection.OnPickerOpened();
        }

        private void OnChatModelFlyoutClosed(object sender, object e) => ModelSelection.OnPickerClosed();

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
        /// Builds the reasoning effort list.
        /// </summary>
        /// <remarks>
        /// The rows are the same shape the model picker uses, and deliberately so: one
        /// style from App.xaml, the same 28px rhythm, the same rule in the margin for the
        /// row in use, the same cursor fill for the row the keys are on. Two option lists
        /// in one toolbar answering the same question should not be two designs, and a
        /// shared style makes the next change to one a change to both rather than
        /// something somebody has to remember.
        ///
        /// The check glyph is gone for the reason it is gone from the model picker: a rule
        /// in the margin is found faster in a list that moves, and the check was a second
        /// signal for a fact the rule already carries. It was also not doing what its
        /// comment claimed. The old row held the check in an auto-width column and
        /// collapsed it when the row was not selected, so the label did shift sideways by
        /// the width of a check on every state change; "always present" was true of the
        /// object and false of the layout.
        ///
        /// No descriptions. "low" and "high" are the levels the provider publishes, and
        /// any sentence written under them about what they feel like would be prose nobody
        /// can check. The values are the interface.
        ///
        /// The container takes the focus and the rows are not tab stops, so Tab leaves the
        /// flyout rather than walking four buttons to get out of it.
        /// </remarks>
        private void OnChatReasoningFlyoutOpened(object sender, object e)
        {
            ReasoningOptionsContainer.Children.Clear();
            _reasoningRows.Clear();
            _reasoningEfforts.Clear();
            var options = ModelSelection.ReasoningOptions();

            var resources = Application.Current.Resources;
            var font = (FontFamily)resources["RuUiFontFamily"];
            var ink = (Brush)resources["RuInkBrush"];
            var accent = (Brush)resources["RuAccentBrush"];
            _reasoningCursorBrush = (Brush)resources["RuAccentSoftBrush"];
            _reasoningRestBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var rowStyle = (Style)resources["RuOptionRowStyle"];

            int selectedIndex = 0;
            foreach (var option in options)
            {
                bool isSelected = string.Equals(option.Value, ModelSelection.SelectedEffort, StringComparison.OrdinalIgnoreCase);
                string label = option.Label ?? option.Value;
                int index = _reasoningRows.Count;
                if (isSelected)
                {
                    selectedIndex = index;
                }

                var btn = new Button
                {
                    Style = rowStyle,
                    Background = _reasoningRestBrush,
                    Tag = index,
                };
                AutomationProperties.SetName(btn, $"Reasoning effort: {label}");
                btn.PointerEntered += OnReasoningRowPointerEntered;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ReasoningSelectionColumnWidth) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                if (isSelected)
                {
                    var bar = new Border
                    {
                        Width = 3,
                        Background = accent,
                        Margin = new Thickness(2, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Stretch,
                    };
                    Grid.SetColumn(bar, 0);
                    grid.Children.Add(bar);
                }

                var titleBlock = new TextBlock
                {
                    Text = label,
                    FontFamily = font,
                    FontSize = 13,
                    Foreground = ink,
                    FontWeight = isSelected
                        ? Microsoft.UI.Text.FontWeights.SemiBold
                        : Microsoft.UI.Text.FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(titleBlock, 1);
                grid.Children.Add(titleBlock);

                btn.Content = grid;
                btn.Click += OnReasoningOptionClicked;
                ReasoningOptionsContainer.Children.Add(btn);
                _reasoningRows.Add(btn);
                _reasoningEfforts.Add(option.Value);
            }

            _reasoningKeyboard = new OptionListKeyboard(
                _reasoningRows.Count,
                ActivateReasoningRow,
                () => ChatReasoningFlyout.Hide());
            _reasoningKeyboard.CursorChanged += OnReasoningCursorChanged;
            _reasoningKeyboard.MoveCursor(selectedIndex);

            ReasoningOptionsContainer.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// The pointer is the cursor, exactly as in the model picker: one marked row, so
        /// "what will Enter pick" has a single answer whichever device the user reached
        /// for.
        /// </summary>
        private void OnReasoningRowPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button { Tag: int index })
            {
                _reasoningKeyboard.MoveCursor(index, show: false);
            }
        }

        private void OnReasoningCursorChanged(int index)
        {
            for (int i = 0; i < _reasoningRows.Count; i++)
            {
                _reasoningRows[i].Background = i == index ? _reasoningCursorBrush : _reasoningRestBrush;
            }
        }

        private void OnReasoningContainerKeyDown(object sender, KeyRoutedEventArgs e)
        {
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
