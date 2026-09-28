using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed class ProviderModelSelectionEventArgs : EventArgs
    {
        public ProviderModelSelectionEventArgs(string backend, string model)
        {
            Backend = backend;
            Model = model;
        }

        public string Backend { get; }

        public string Model { get; }
    }

    public sealed partial class ProviderModelPicker : UserControl
    {
        /// <summary>
        /// Width reserved for the published context figure, so the figures in the right
        /// column line up instead of ending wherever their digits happen to end.
        /// </summary>
        /// <remarks>
        /// Fixed rather than automatic because the column holds a measurement and a
        /// measurement column that shifts is not a column. It also holds the value when
        /// the provider published no figure at all, which is most rows on models.dev:
        /// an absent figure must not push the figures above it sideways.
        ///
        /// 40 is sized to the widest thing the formatter can emit, not to a sample.
        /// FormatTokens caps at "0.#", so the longest output is 999.9K or 999.9M, six
        /// characters, about 34px at this size. A column narrower than that would start
        /// trimming the only number on the row.
        /// </remarks>
        private const double ContextColumnWidth = 40;

        /// <summary>
        /// Width held by the margin beside the selection bar, whether or not this row
        /// is the selected one.
        /// </summary>
        /// <remarks>
        /// The bar is only drawn on the selected row, so without a reserved column every
        /// other row's name would start a few pixels further left and the column of names
        /// would be ragged down its left edge. That is the same defect the context
        /// figure had when its trailing margin changed on the selected row: a value
        /// column that moves is not a column.
        ///
        /// 3px of bar and 8px of air. The 8 is the gap between the bar and the name; it
        /// is set once here rather than as a margin on the bar so the reserved width and
        /// the drawn width cannot drift apart.
        /// </remarks>
        private const double SelectionColumnWidth = 11;

        public ProviderModelPicker()
        {
            this.InitializeComponent();
            _brushes = PickerBrushes.Resolve();
            PickerSearchBox.GotFocus += OnSearchFocusChanged;
            PickerSearchBox.LostFocus += OnSearchFocusChanged;
        }

        public event EventHandler<string>? SearchTextChanged;

        public event EventHandler<ProviderModelSelectionEventArgs>? ModelSelected;

        /// <summary>Raised when the user backs out of the list without choosing.</summary>
        public event EventHandler? DismissRequested;

        private ProviderModelPickerProjection? _lastProjection;
        private readonly PickerBrushes _brushes;
        private readonly List<Button> _rowButtons = new();
        private readonly List<ProviderModelPickerRow> _rowModels = new();
        private OptionListKeyboard _keyboard = new(0, _ => { }, () => { });

        public string SearchText => PickerSearchBox.Text;

        public void Apply(ProviderModelPickerProjection projection)
        {
            // A poll can hand back exactly what the last one returned, and the picker
            // is rebuilt on every publish. Rebuilding an identical tree costs the UI
            // thread a thousand control constructions for nothing.
            if (projection.HasSameContentAs(_lastProjection))
            {
                return;
            }

            _lastProjection = projection;
            PickerListContainer.Children.Clear();
            _rowButtons.Clear();
            _rowModels.Clear();

            if (projection.Groups.Count == 0)
            {
                PickerListContainer.Children.Add(
                    CreateEmptyStateText(projection.EmptyStateText));
                RebuildKeyboard();
                return;
            }

            for (int i = 0; i < projection.Groups.Count; i++)
            {
                var group = projection.Groups[i];
                PickerListContainer.Children.Add(CreateGroupHeading(group, i == 0));
                foreach (var row in group.Rows)
                {
                    // The index is fixed before the button exists so the click handler
                    // closes over it. Looking the row up in the list on click would be a
                    // linear scan over several hundred records, and would find the wrong
                    // row if two of them were ever equal.
                    _rowModels.Add(row);
                    int index = _rowModels.Count - 1;
                    var button = CreateRowButton(row, index);
                    PickerListContainer.Children.Add(button);
                    _rowButtons.Add(button);
                }
            }

            RebuildKeyboard();
        }

        public void FocusSearch()
        {
            PickerSearchBox.Focus(FocusState.Programmatic);
            StartCursorOnSelection();
        }

        /// <summary>
        /// Puts the cursor on the row that is in use, so arrowing starts from the value
        /// the flyout is showing rather than from the top of a list the user has never
        /// seen.
        /// </summary>
        public void StartCursorOnSelection()
        {
            for (int i = 0; i < _rowModels.Count; i++)
            {
                if (_rowModels[i].IsSelected)
                {
                    _keyboard.MoveCursor(i);
                    return;
                }
            }

            _keyboard.MoveCursor(0);
        }

        /// <summary>
        /// Rebinds the key controller to the rows just built.
        /// </summary>
        /// <remarks>
        /// A new controller per rebuild rather than a resettable one: the flyout is
        /// rebuilt whenever the catalog publishes, the row list is a fresh list each
        /// time, and a controller that outlived its rows would be one more piece of
        /// state to keep in step. The cursor is restored to the selected row because a
        /// rebuild caused by a background poll must not move the row the user is on.
        /// </remarks>
        private void RebuildKeyboard()
        {
            _keyboard = new OptionListKeyboard(
                _rowButtons.Count,
                ActivateRowAt,
                () => DismissRequested?.Invoke(this, EventArgs.Empty),
                // The list scrolls inside a ScrollViewer this view owns and the
                // controller has no business knowing its offset, so it is told which row
                // and asked to fetch it. A row that is merely adjacent is not on screen.
                index => _rowButtons[index].StartBringIntoView());
            _keyboard.CursorChanged += OnCursorChanged;
            StartCursorOnSelection();
        }

        private void ActivateRowAt(int index)
        {
            if (index < 0 || index >= _rowModels.Count)
            {
                return;
            }

            var row = _rowModels[index];
            ModelSelected?.Invoke(
                this,
                new ProviderModelSelectionEventArgs(row.Backend, row.ModelId));
        }

        /// <summary>
        /// Repaints the cursor, and only the cursor.
        /// </summary>
        /// <remarks>
        /// The committed selection is drawn by the row itself and is not touched here:
        /// a background catalog publish that happens to move the cursor must never
        /// repaint the row the user has actually chosen.
        /// </remarks>
        private void OnCursorChanged(int index)
        {
            for (int i = 0; i < _rowButtons.Count; i++)
            {
                _rowButtons[i].Background = i == index ? _brushes.Cursor : _brushes.Rest;
            }
        }

        private void OnPickerKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Space is a character here: the focus is in the search field, and a user
            // pressing it is typing "gpt 4o", not picking a row. Swallowing the key also
            // stops a single-line field from moving its own caret while the user is
            // arrowing through rows.
            if (_keyboard.HandleKey(e.Key, spaceActivates: false))
            {
                e.Handled = true;
            }
        }

        private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            // The pointer is the cursor. One marked row means one answer to "what will
            // Enter choose", whether it got there with the mouse or with the arrows. The
            // row carries its own index because the list is rebuilt on every catalog
            // publish and a handler that searched the list would be a scan per mouse move.
            if (sender is Button { Tag: int index })
            {
                _keyboard.MoveCursor(index, show: false);
            }
        }

        private void OnPickerSearchTextChanged(object sender, TextChangedEventArgs e) =>
            SearchTextChanged?.Invoke(this, PickerSearchBox.Text);

        /// <summary>
        /// Repaints the field's rule when it takes or loses the caret.
        /// </summary>
        /// <remarks>
        /// The TextBox carries no border of its own, so without this there is no cue at
        /// all that the field is focused: a transparent field in a list of rows looks
        /// like a label. The accent is the app's own, and a rule rather than a glow,
        /// because the field is already bounded on three sides by nothing at all.
        /// </remarks>
        private void OnSearchFocusChanged(object sender, RoutedEventArgs e)
        {
            PickerSearchField.Background = PickerSearchBox.FocusState == FocusState.Unfocused
                ? _brushes.Line
                : _brushes.Accent;
        }

        /// <summary>
        /// Resolved once instead of per row. The previous shape read these from
        /// Application.Current resources inside every row builder, which is five
        /// dictionary lookups for every control on every rebuild.
        /// </summary>
        private sealed record PickerBrushes(
            FontFamily FontFamily,
            Brush Ink,
            Brush Muted,
            Brush Line,
            Brush Accent,
            Brush Warning,
            Brush Danger,
            Brush Rest,
            Brush Cursor,
            Style RowStyle)
        {
            public static PickerBrushes Resolve()
            {
                var resources = Application.Current.Resources;
                return new PickerBrushes(
                    (FontFamily)resources["RuUiFontFamily"],
                    (Brush)resources["RuInkBrush"],
                    (Brush)resources["RuMutedBrush"],
                    (Brush)resources["RuLineBrush"],
                    (Brush)resources["RuAccentBrush"],
                    (Brush)resources["RuWarningBrush"],
                    (Brush)resources["RuDangerBrush"],
                    new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    // The app already uses this for a hovered dropdown row, so the
                    // keyboard cursor is the colour a pointer row has always been
                    // rather than a fifth grey introduced for the keys.
                    (Brush)resources["RuAccentSoftBrush"],
                    (Style)resources["RuOptionRowStyle"]);
            }
        }

        private TextBlock CreateEmptyStateText(string text) => new()
        {
            Text = text,
            FontFamily = _brushes.FontFamily,
            FontSize = 12,
            Foreground = _brushes.Muted,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 18),
        };

        /// <summary>
        /// The provider's name, how many of its models are on screen, and whether the
        /// catalog behind them is stale.
        /// </summary>
        /// <remarks>
        /// The count sits against the name, not on the right edge. The right edge is the
        /// context column, and a bare 12 flush with a column of token figures reads as
        /// one more of them: 12 what, at 12 what, next to the model it is describing.
        /// Beside the name it is plainly a count, and the right edge is left to hold one
        /// kind of thing, which is what makes a column of numbers scannable at all.
        ///
        /// Staleness is stated once, here, because it is a fact about the provider's
        /// catalog: it used to be repeated on every row of a stale group, where on a
        /// long model id it also sat behind the ellipsis and was never read.
        ///
        /// Groups are separated by space rather than by a rule. A hairline between each
        /// pair is the weakest divider available and the panel already had three
        /// others, so the list read as a stack of bands instead of one column of names.
        /// </remarks>
        private Border CreateGroupHeading(ProviderModelPickerGroup group, bool isFirstGroup)
        {
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                FontFamily = _brushes.FontFamily,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = _brushes.Muted,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // One TextBlock rather than a name and a count joined with a separator: the
            // provider names are short and the count is two or three characters, so the
            // pair can never need trimming, and a joined string cannot come apart.
            name.Text = group.Rows.Count == 1
                ? group.Provider.DisplayName
                : $"{group.Provider.DisplayName} · {group.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            Grid.SetColumn(name, 0);
            heading.Children.Add(name);

            if (group.IsStale)
            {
                var stale = new TextBlock
                {
                    Text = ProviderModelPickerProjection.StaleAnnotation,
                    FontFamily = _brushes.FontFamily,
                    FontSize = 10,
                    Foreground = _brushes.Warning,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(stale, 1);
                heading.Children.Add(stale);
            }

            return new Border
            {
                Child = heading,
                Padding = new Thickness(11, isFirstGroup ? 3 : 11, 6, 2),
                IsHitTestVisible = false,
            };
        }

        /// <summary>
        /// One model: a rule in the margin when it is the one in use, the name, whatever
        /// state applies to the name itself, and the published context figure.
        /// </summary>
        /// <remarks>
        /// The rule is a mark in the margin rather than a filled row. A fill is one more
        /// grey competing with the cursor grey, and this list scrolls: a 3px bar at the
        /// edge of a row is found by the eye in a fraction of the time a tint of grey
        /// is, and it says the same thing without adding a surface to a panel that
        /// already had five.
        ///
        /// The context figure is plain text in a fixed column, not a bordered lozenge. As
        /// a lozenge it was a fourth value competing with the name, and it shifted
        /// sideways on the selected row because its trailing margin changed, so the
        /// figures never lined up. Nothing is drawn for a figure the provider did not
        /// publish: the column stays and the cell is empty, which is the difference
        /// between "no context on record" and a guess.
        /// </remarks>
        private Button CreateRowButton(ProviderModelPickerRow row, int index)
        {
            var itemButton = new Button
            {
                Style = _brushes.RowStyle,
                Background = _brushes.Rest,
                IsEnabled = row.IsEnabled,
                Tag = index,
            };

            itemButton.PointerEntered += OnRowPointerEntered;

            var itemGrid = new Grid
            {
                IsHitTestVisible = false // Prevents child elements from bubbling micro-pointer events
            };
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SelectionColumnWidth) });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ContextColumnWidth) });

            if (row.IsSelected)
            {
                // Inset far enough from the row's own rounded corner that neither end
                // pokes out of the curve, and square-ended: a 3px bar with rounded caps
                // is a lozenge, and this panel does not use lozenges.
                var tick = new Border
                {
                    Width = 3,
                    Background = _brushes.Accent,
                    Margin = new Thickness(2, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Grid.SetColumn(tick, 0);
                itemGrid.Children.Add(tick);
            }

            // The name and its state share one cell: the name gives way, the state does
            // not. Read as a single string they were one string, and a 12-character
            // ellipsis then cut the state off the end of a long model id, so the one
            // thing the row was warning about was the part that never showed.
            var nameCell = new Grid();
            nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameCell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = row.ModelId,
                FontFamily = _brushes.FontFamily,
                FontSize = 13,
                Foreground = row.IsEnabled ? _brushes.Ink : _brushes.Muted,
                FontWeight = row.IsSelected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(nameText, 0);
            nameCell.Children.Add(nameText);

            if (!string.IsNullOrWhiteSpace(row.Annotation))
            {
                var state = new TextBlock
                {
                    Text = row.Annotation,
                    FontFamily = _brushes.FontFamily,
                    FontSize = 10,
                    Foreground = _brushes.Danger,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(state, 1);
                nameCell.Children.Add(state);
            }

            Grid.SetColumn(nameCell, 1);
            itemGrid.Children.Add(nameCell);

            var context = new TextBlock
            {
                Text = row.ContextLabel,
                FontFamily = _brushes.FontFamily,
                FontSize = 11,
                Foreground = _brushes.Muted,
                HorizontalAlignment = HorizontalAlignment.Right,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(context, 2);
            itemGrid.Children.Add(context);

            itemButton.Content = itemGrid;

            var automationText = ProviderModelPickerProjection.RowAutomationText(row);
            AutomationProperties.SetName(itemButton, automationText);

            itemButton.Click += (s, e) => ActivateRowAt(index);

            return itemButton;
        }
    }
}
