using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RuApp.Services;

namespace RuApp.Controls
{
    /// <summary>
    /// Three dots pulsing while an assistant reply waits for its first token.
    /// The pulse runs on Composition (a staggered opacity keyframe per dot) so no
    /// timer repaints it, and it is skipped entirely when the reader has
    /// animations off — the dots then sit at a readable static opacity.
    /// </summary>
    public sealed class TypingDotsControl : UserControl
    {
        private const int DotCount = 3;
        private const double DotSize = 6;
        private const double DotSpacing = 4;
        private const double PulseMilliseconds = 1200;
        private const double DotStaggerMilliseconds = 180;
        private const float StaticOpacity = 0.45f;

        private readonly Ellipse[] _dots = new Ellipse[DotCount];

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(
                nameof(IsActive),
                typeof(bool),
                typeof(TypingDotsControl),
                new PropertyMetadata(false, OnIsActiveChanged));

        /// <summary>True while the dots should show and pulse.</summary>
        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public TypingDotsControl()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = DotSpacing,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var fill = ResolveMutedBrush();
            for (int index = 0; index < DotCount; index++)
            {
                var dot = new Ellipse
                {
                    Width = DotSize,
                    Height = DotSize,
                    Fill = fill,
                    Opacity = 0,
                };
                _dots[index] = dot;
                row.Children.Add(dot);
            }

            Content = row;
            Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(this, "Assistant is typing");
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((TypingDotsControl)d).Apply((bool)e.NewValue);

        private void Apply(bool active)
        {
            Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (!active)
            {
                StopPulse();
                return;
            }

            if (UiTiming.AnimationsEnabled)
            {
                StartPulse();
            }
            else
            {
                StopPulse();
                foreach (var dot in _dots)
                {
                    dot.Opacity = StaticOpacity;
                }
            }
        }

        private void StartPulse()
        {
            for (int index = 0; index < _dots.Length; index++)
            {
                try
                {
                    var visual = ElementCompositionPreview.GetElementVisual(_dots[index]);
                    var compositor = visual.Compositor;
                    var pulse = compositor.CreateScalarKeyFrameAnimation();
                    pulse.InsertKeyFrame(0f, StaticOpacity);
                    pulse.InsertKeyFrame(0.5f, 1f);
                    pulse.InsertKeyFrame(1f, StaticOpacity);
                    pulse.Duration = TimeSpan.FromMilliseconds(PulseMilliseconds);
                    pulse.IterationBehavior = AnimationIterationBehavior.Forever;
                    pulse.DelayTime = TimeSpan.FromMilliseconds(index * DotStaggerMilliseconds);
                    visual.Opacity = StaticOpacity;
                    visual.StartAnimation("Opacity", pulse);
                }
                catch (Exception error)
                {
                    GuiDiagnosticWriter.AppendTraceLine($"[TypingDotsControl] Pulse failed: {error.Message}");
                    _dots[index].Opacity = StaticOpacity;
                }
            }
        }

        private void StopPulse()
        {
            foreach (var dot in _dots)
            {
                try
                {
                    ElementCompositionPreview.GetElementVisual(dot).StopAnimation("Opacity");
                }
                catch (Exception error)
                {
                    GuiDiagnosticWriter.AppendTraceLine($"[TypingDotsControl] Stop failed: {error.Message}");
                }

                dot.Opacity = 0;
            }
        }

        private static Brush ResolveMutedBrush()
        {
            if (Application.Current?.Resources.TryGetValue("RuMutedBrush", out object? value) == true
                && value is Brush brush)
            {
                return brush;
            }

            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184));
        }
    }
}
