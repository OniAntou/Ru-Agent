using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace RuApp.Services
{
    /// <summary>
    /// Shared shell motion for surfaces that appear, leave and swap content: a
    /// fade-and-lift entrance, a fade-and-drop exit that reports back once the
    /// element has left the screen, and a dip-faded content swap. Composition runs
    /// the animations off the UI thread, and every entry point degrades to the plain
    /// state change when the reader has animations off, so callers keep one path.
    /// </summary>
    public static class UiMotion
    {
        public const double EntranceMilliseconds = 160;
        public const double ExitMilliseconds = 120;
        public const double SwapOutMilliseconds = 100;
        public const double SwapInMilliseconds = 140;
        public const double BrushFadeMilliseconds = 140;
        public const double StateFadeMilliseconds = 120;
        private const double RiseDistance = 8;

        /// <summary>
        /// The collapse callback runs after the animation plus this slack, so the
        /// element is already invisible when the layout drops it.
        /// </summary>
        private const double CompletionSlackMilliseconds = 40;

        /// <summary>Fades an element in and lifts it into place.</summary>
        public static void Enter(FrameworkElement element)
        {
            ArgumentNullException.ThrowIfNull(element);
            if (!UiTiming.AnimationsEnabled)
            {
                Reset(element);
                return;
            }

            try
            {
                var visual = ElementVisual(element);
                visual.Opacity = 0f;
                visual.Offset = new Vector3(0f, (float)RiseDistance, 0f);
                Animate(visual, toOpacity: 1f, translationY: 0f, EntranceMilliseconds);
            }
            catch (Exception error)
            {
                Trace("enter", error);
                Reset(element);
            }
        }

        /// <summary>
        /// Fades an element out and drops it, then runs <paramref name="completed"/>
        /// — typically collapsing the element. Callers re-check their desired state
        /// in the callback, because a surface can be asked back before it has gone.
        /// </summary>
        public static void Exit(FrameworkElement element, Action? completed = null)
        {
            ArgumentNullException.ThrowIfNull(element);
            if (!UiTiming.AnimationsEnabled)
            {
                Reset(element);
                completed?.Invoke();
                return;
            }

            try
            {
                var visual = ElementVisual(element);
                Animate(visual, toOpacity: 0f, translationY: (float)RiseDistance, ExitMilliseconds);
                After(ExitMilliseconds + CompletionSlackMilliseconds, completed);
            }
            catch (Exception error)
            {
                Trace("exit", error);
                Reset(element);
                completed?.Invoke();
            }
        }

        /// <summary>
        /// Dip-fades a host around a content swap: the host fades out, the swap runs
        /// while it is invisible, and it fades back in. A swap that throws still
        /// leaves the host visible.
        /// </summary>
        public static void SwapContent(FrameworkElement host, Action swap)
        {
            ArgumentNullException.ThrowIfNull(host);
            ArgumentNullException.ThrowIfNull(swap);
            if (!UiTiming.AnimationsEnabled)
            {
                Reset(host);
                swap();
                return;
            }

            try
            {
                var visual = ElementVisual(host);
                Animate(visual, toOpacity: 0f, translationY: 0f, SwapOutMilliseconds);
                After(SwapOutMilliseconds, () =>
                {
                    swap();
                    Animate(visual, toOpacity: 1f, translationY: 0f, SwapInMilliseconds);
                });
            }
            catch (Exception error)
            {
                Trace("swap", error);
                Reset(host);
                swap();
            }
        }

        /// <summary>
        /// Shows or hides a small panel: showing plays the entrance once, hiding
        /// collapses immediately because the content around a small panel reflows
        /// either way and a delayed collapse would race the next show.
        /// </summary>
        public static void SetVisible(FrameworkElement? element, bool visible)
        {
            if (element is null)
            {
                return;
            }

            if (!visible)
            {
                element.Visibility = Visibility.Collapsed;
                return;
            }

            bool wasVisible = element.Visibility == Visibility.Visible;
            element.Visibility = Visibility.Visible;
            if (!wasVisible)
            {
                Enter(element);
            }
        }

        /// <summary>
        /// Cross-fades one brush-valued property to <paramref name="target"/>. The
        /// theme brushes are shared instances, so animating one of those would repaint
        /// every element that uses it: each fade builds a private solid-colour brush
        /// and animates that instead. Non-solid targets (a gradient, or a reader with
        /// animations off) are applied instantly.
        /// </summary>
        public static void FadeBrush(DependencyObject owner, DependencyProperty property, Brush? target)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(property);

            if (!UiTiming.AnimationsEnabled || target is not null and not SolidColorBrush)
            {
                owner.SetValue(property, target);
                return;
            }

            // Read the live colour first: replacing the brush strands the old one,
            // and with it the animated value a second fade would start from.
            var from = (owner.GetValue(property) as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Transparent;
            var to = (target as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Transparent;
            if (from == to)
            {
                return;
            }

            try
            {
                var brush = new SolidColorBrush(from);
                owner.SetValue(property, brush);
                var animation = new ColorAnimation
                {
                    To = to,
                    Duration = TimeSpan.FromMilliseconds(BrushFadeMilliseconds),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(animation, brush);
                Storyboard.SetTargetProperty(animation, "Color");
                var storyboard = new Storyboard();
                storyboard.Children.Add(animation);
                storyboard.Begin();
            }
            catch (Exception error)
            {
                Trace("brush fade", error);
                owner.SetValue(property, target);
            }
        }

        /// <summary>Fades one element's opacity to a target; instant with animations off.</summary>
        public static void FadeOpacity(FrameworkElement element, double target)
        {
            ArgumentNullException.ThrowIfNull(element);
            if (!UiTiming.AnimationsEnabled)
            {
                element.Opacity = target;
                return;
            }

            try
            {
                var fade = new DoubleAnimation
                {
                    To = target,
                    Duration = TimeSpan.FromMilliseconds(StateFadeMilliseconds),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(fade, element);
                Storyboard.SetTargetProperty(fade, "Opacity");
                var storyboard = new Storyboard();
                storyboard.Children.Add(fade);
                storyboard.Begin();
            }
            catch (Exception error)
            {
                Trace("opacity fade", error);
                element.Opacity = target;
            }
        }

        /// <summary>Returns an element to full opacity and no translation.</summary>
        public static void Reset(FrameworkElement element)
        {
            ArgumentNullException.ThrowIfNull(element);
            try
            {
                var visual = ElementVisual(element);
                visual.StopAnimation("Opacity");
                visual.StopAnimation("Offset");
                visual.Opacity = 1f;
                visual.Offset = Vector3.Zero;
            }
            catch (Exception error)
            {
                Trace("reset", error);
                element.Opacity = 1;
            }
        }

        /// <summary>
        /// The XAML element's container visual. <c>GetElementVisual</c> is typed as
        /// <see cref="Visual"/>, but a XAML element always hands back a container,
        /// which is what carries <c>Offset</c> — the composition property behind a
        /// translation (the XAML <c>Translation</c> property has no composition twin).
        /// </summary>
        private static ContainerVisual ElementVisual(FrameworkElement element) =>
            (ContainerVisual)ElementCompositionPreview.GetElementVisual(element);

        private static void Animate(ContainerVisual visual, float toOpacity, float translationY, double milliseconds)
        {
            var compositor = visual.Compositor;
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, toOpacity, ease);
            fade.Duration = duration;
            visual.StartAnimation("Opacity", fade);

            var glide = compositor.CreateVector3KeyFrameAnimation();
            glide.InsertKeyFrame(1f, new Vector3(0f, translationY, 0f), ease);
            glide.Duration = duration;
            visual.StartAnimation("Offset", glide);
        }

        /// <summary>
        /// One-shot completion timers, held so they cannot be collected while they
        /// run; the DispatcherQueue owns a timer only while it is ticking.
        /// </summary>
        private static readonly List<DispatcherQueueTimer> PendingCompletions = new();

        private static void After(double milliseconds, Action? completed)
        {
            if (completed is null)
            {
                return;
            }

            var queue = DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                completed();
                return;
            }

            var timer = queue.CreateTimer();
            timer.IsRepeating = false;
            timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
            lock (PendingCompletions)
            {
                PendingCompletions.Add(timer);
            }

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                lock (PendingCompletions)
                {
                    PendingCompletions.Remove(timer);
                }

                try
                {
                    completed();
                }
                catch (Exception error)
                {
                    Trace("completion", error);
                }
            };
            timer.Start();
        }

        private static void Trace(string what, Exception error) =>
            GuiDiagnosticWriter.AppendTraceLine($"[UiMotion] {what} failed: {error.Message}");
    }
}
