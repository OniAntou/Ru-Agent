using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RuApp.Services;
using Windows.Foundation;
using Windows.UI;

namespace RuApp.Controls
{
    /// <summary>One message on the rail: its caption and how to preview it on hover.</summary>
    public readonly record struct MessageRailEntry(Func<MessageRailPreview> Preview, string Caption);

    /// <summary>
    /// A column of ticks in the transcript gutter, one per message (grouped
    /// when the transcript is too long), that previews a message on hover and
    /// jumps to it on click.
    /// </summary>
    /// <remarks>
    /// The ticks are drawn shapes on one hit-test surface rather than buttons:
    /// the pointer's Y picks the nearest tick, so a dense rail stays steady to
    /// hover. The card never takes hit tests, so it cannot steal the pointer
    /// from the rail and set off a show/hide loop.
    ///
    /// At rest every tick is the same short, dim mark; only the one in view is
    /// brighter. Hovering lifts the tick under the pointer and, less, its
    /// neighbours, by scaling toward the transcript rather than relaying out.
    /// </remarks>
    public sealed partial class MessageRailControl : Grid
    {
        private const double TickWidth = 8;
        private const double TickThickness = 2;
        private const double TickLeft = 6;
        private const double RestingOpacity = 0.4;
        private const double CardWidth = 300;
        private const double CardGap = 8;
        private const double EdgeInset = 8;
        private const double VerticalInset = 24;

        private static readonly SolidColorBrush FallbackInkBrush = new(Color.FromArgb(255, 241, 245, 249));
        private static readonly SolidColorBrush FallbackMutedBrush = new(Color.FromArgb(255, 148, 163, 184));
        private static readonly SolidColorBrush FallbackSurfaceBrush = new(Color.FromArgb(255, 38, 38, 38));
        private static readonly SolidColorBrush FallbackLineBrush = new(Color.FromArgb(255, 64, 64, 64));

        private readonly Canvas _ticks = new()
        {
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Width = MessageRailPolicy.RailWidth,
        };
        private readonly Canvas _cardLayer = new() { IsHitTestVisible = false };
        private readonly Border _card;
        private readonly TextBlock _cardCaption;
        private readonly TextBlock _cardTitle;
        private readonly TextBlock _cardBody;
        private readonly List<Rectangle> _tickShapes = new();
        private IReadOnlyList<MessageRailEntry> _entries = Array.Empty<MessageRailEntry>();
        private int _tickCount;
        private double _pitch = MessageRailPolicy.MaxPitch;
        private int _currentMessage = -1;
        private int _currentTick = -1;
        private int _hoverTick = -1;

        /// <summary>Raised with the index of the first message the clicked tick stands for.</summary>
        public event EventHandler<int>? MessageInvoked;

        public MessageRailControl()
        {
            Width = MessageRailPolicy.RailWidth;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Stretch;
            AutomationProperties.SetName(this, "Message navigation");
            AutomationProperties.SetAutomationId(this, "MessageRail");

            _cardCaption = new TextBlock
            {
                FontSize = 11.5,
                Foreground = Brush("RuMutedBrush", FallbackMutedBrush),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _cardTitle = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("RuInkBrush", FallbackInkBrush),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
            };
            _cardBody = new TextBlock
            {
                FontSize = 12.5,
                Foreground = Brush("RuMutedBrush", FallbackMutedBrush),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 3,
            };
            if (Application.Current?.Resources.TryGetValue("RuUiFontFamily", out var font) == true && font is FontFamily family)
            {
                _cardCaption.FontFamily = family;
                _cardTitle.FontFamily = family;
                _cardBody.FontFamily = family;
            }

            var cardText = new StackPanel { Spacing = 3 };
            cardText.Children.Add(_cardCaption);
            cardText.Children.Add(_cardTitle);
            cardText.Children.Add(_cardBody);
            _card = new Border
            {
                Width = CardWidth,
                Padding = new Thickness(12, 10, 12, 10),
                Background = Brush("RuSurfaceBrush", FallbackSurfaceBrush),
                BorderBrush = Brush("RuLineBrush", FallbackLineBrush),
                BorderThickness = new Thickness(1),
                CornerRadius = UiCornerRadius.Card,
                Child = cardText,
                Visibility = Visibility.Collapsed,
            };
            Canvas.SetLeft(_card, MessageRailPolicy.RailWidth + CardGap);
            _cardLayer.Children.Add(_card);

            Children.Add(_cardLayer);
            Children.Add(_ticks);

            _ticks.PointerMoved += OnTicksPointerMoved;
            _ticks.PointerExited += OnTicksPointerExited;
            _ticks.PointerCanceled += OnTicksPointerExited;
            _ticks.Tapped += OnTicksTapped;
            SizeChanged += (_, _) => Layout();
        }

        public int MessageCount => _entries.Count;

        /// <summary>Replaces the rail's messages; the previews are read only when hovered.</summary>
        public void SetEntries(IReadOnlyList<MessageRailEntry> entries)
        {
            _entries = entries ?? Array.Empty<MessageRailEntry>();
            HideCard();
            Layout();
        }

        /// <summary>Highlights the tick that holds the message the reader is on.</summary>
        public void SetCurrentMessage(int messageIndex)
        {
            _currentMessage = messageIndex;
            var tick = MessageRailPolicy.TickOfMessage(messageIndex, _tickCount, _entries.Count);
            if (tick == _currentTick) return;
            var previous = _currentTick;
            _currentTick = tick;
            StyleTick(previous);
            StyleTick(tick);
        }

        private void Layout()
        {
            var available = Math.Max(0, ActualHeight - 2 * VerticalInset);
            var tickCount = MessageRailPolicy.TickCount(_entries.Count, available);
            var pitch = MessageRailPolicy.Pitch(tickCount, available);
            _tickCount = tickCount;
            _pitch = pitch;
            _hoverTick = -1;
            _currentTick = _currentMessage < 0 ? -1 : MessageRailPolicy.TickOfMessage(_currentMessage, tickCount, _entries.Count);

            while (_tickShapes.Count < tickCount)
            {
                var shape = new Rectangle
                {
                    Width = TickWidth,
                    Height = TickThickness,
                    RadiusX = 1,
                    RadiusY = 1,
                    IsHitTestVisible = false,
                    // Grow from the left end, toward the transcript.
                    CenterPoint = new Vector3(0, (float)(TickThickness / 2), 0),
                };
                if (UiTiming.AnimationsEnabled)
                    shape.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
                Canvas.SetLeft(shape, TickLeft);
                _tickShapes.Add(shape);
                _ticks.Children.Add(shape);
            }
            for (var i = 0; i < _tickShapes.Count; i++)
            {
                _tickShapes[i].Visibility = i < tickCount ? Visibility.Visible : Visibility.Collapsed;
                if (i < tickCount)
                {
                    Canvas.SetTop(_tickShapes[i], i * pitch + (pitch - TickThickness) / 2);
                    StyleTick(i);
                }
            }
            _ticks.Height = Math.Max(0, tickCount * pitch);
        }

        private void StyleTick(int tick)
        {
            if (tick < 0 || tick >= _tickCount || tick >= _tickShapes.Count) return;
            var shape = _tickShapes[tick];
            var emphasized = tick == _currentTick || tick == _hoverTick;
            shape.Scale = new Vector3((float)MessageRailPolicy.TickScale(tick, _hoverTick, _currentTick), 1, 1);
            shape.Fill = emphasized ? Brush("RuInkBrush", FallbackInkBrush) : Brush("RuMutedBrush", FallbackMutedBrush);
            shape.Opacity = emphasized ? 1 : RestingOpacity;
        }

        private void StyleAllTicks()
        {
            for (var tick = 0; tick < _tickCount; tick++) StyleTick(tick);
        }

        private void OnTicksPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var tick = MessageRailPolicy.TickAt(e.GetCurrentPoint(_ticks).Position.Y, _tickCount, _pitch);
            if (tick == _hoverTick) return;
            _hoverTick = tick;
            StyleAllTicks();
            ShowCard(tick);
        }

        private void OnTicksPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _hoverTick = -1;
            StyleAllTicks();
            HideCard();
        }

        private void OnTicksTapped(object sender, TappedRoutedEventArgs e)
        {
            var tick = MessageRailPolicy.TickAt(e.GetPosition(_ticks).Y, _tickCount, _pitch);
            var message = MessageRailPolicy.FirstMessageOfTick(tick, _tickCount, _entries.Count);
            if (message < 0) return;
            e.Handled = true;
            MessageInvoked?.Invoke(this, message);
        }

        private void ShowCard(int tick)
        {
            var message = MessageRailPolicy.FirstMessageOfTick(tick, _tickCount, _entries.Count);
            if (message < 0)
            {
                HideCard();
                return;
            }

            var entry = _entries[message];
            var preview = entry.Preview();
            _cardCaption.Text = entry.Caption;
            _cardTitle.Text = preview.Title;
            _cardBody.Text = preview.Body;
            _cardBody.Visibility = preview.Body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _card.Visibility = Visibility.Visible;
            _card.Measure(new Size(CardWidth, double.PositiveInfinity));

            // Centre the card on the tick, kept inside the transcript area.
            var ticksTop = (ActualHeight - _ticks.Height) / 2;
            var tickY = ticksTop + tick * _pitch + _pitch / 2;
            var top = tickY - _card.DesiredSize.Height / 2;
            var maxTop = Math.Max(EdgeInset, ActualHeight - _card.DesiredSize.Height - EdgeInset);
            Canvas.SetTop(_card, Math.Clamp(top, EdgeInset, maxTop));
        }

        private void HideCard() => _card.Visibility = Visibility.Collapsed;

        private static Brush Brush(string resourceKey, Brush fallback)
            => Application.Current?.Resources is { } resources
               && resources.TryGetValue(resourceKey, out var value) && value is Brush brush
                ? brush
                : fallback;
    }
}
