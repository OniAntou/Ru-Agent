using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace RuApp.Controls;

/// <summary>
/// DSH-style turn-process disclosure. The header sits above a body whose height
/// animates between the compact answer and the full process; the body is measured
/// after the content binding changes so collapse/expand never flashes an empty row.
/// </summary>
public sealed partial class TurnProcessDisclosure : UserControl
{
    public static readonly DependencyProperty BodyContentProperty = DependencyProperty.Register(
        nameof(BodyContent), typeof(object), typeof(TurnProcessDisclosure),
        new PropertyMetadata(null, OnBodyContentChanged));

    public static readonly DependencyProperty HeaderTextProperty = DependencyProperty.Register(
        nameof(HeaderText), typeof(string), typeof(TurnProcessDisclosure),
        new PropertyMetadata(string.Empty, OnHeaderChanged));

    public static readonly DependencyProperty HeaderVisibleProperty = DependencyProperty.Register(
        nameof(HeaderVisible), typeof(bool), typeof(TurnProcessDisclosure),
        new PropertyMetadata(false, OnHeaderChanged));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(TurnProcessDisclosure),
        new PropertyMetadata(false, OnExpandedChanged));

    private bool _loaded;
    private bool _animating;
    private double _pendingFrom;
    private Storyboard? _storyboard;
    private static readonly UISettings UiSettings = new();

    public TurnProcessDisclosure()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loaded = true;
            UpdateHeader();
            SetBodyAutoHeight();
        };
        Unloaded += (_, _) => StopAnimation();
    }

    public object? BodyContent
    {
        get => GetValue(BodyContentProperty);
        set => SetValue(BodyContentProperty, value);
    }

    public string HeaderText
    {
        get => (string)GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

    public bool HeaderVisible
    {
        get => (bool)GetValue(HeaderVisibleProperty);
        set => SetValue(HeaderVisibleProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    private static void OnBodyContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is TurnProcessDisclosure disclosure && disclosure._loaded && !disclosure._animating)
            disclosure.SetBodyAutoHeight();
    }

    private static void OnHeaderChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is TurnProcessDisclosure disclosure)
            disclosure.UpdateHeaderAndAnimate();
    }

    private static void OnExpandedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is TurnProcessDisclosure disclosure)
            disclosure.UpdateHeaderAndAnimate();
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        // The body may still be at its previous height when the binding updates the
        // content, so capture it before flipping the state.
        _pendingFrom = BodyHost.ActualHeight;
        IsExpanded = !IsExpanded;
    }

    private void UpdateHeaderAndAnimate()
    {
        UpdateHeader();
        if (!_loaded) return;
        BeginTransition();
    }

    private void UpdateHeader()
    {
        HeaderButton.Visibility = HeaderVisible ? Visibility.Visible : Visibility.Collapsed;
        HeaderTextBlock.Text = HeaderText;
        ChevronIcon.Glyph = IsExpanded ? "\uE70E" : "\uE70D";
        AutomationProperties.SetName(HeaderButton, $"{HeaderText}. Toggle turn process.");
    }

    private void BeginTransition()
    {
        var from = _pendingFrom > 0 ? _pendingFrom : BodyHost.ActualHeight;
        _pendingFrom = 0;
        if (!UiSettings.AnimationsEnabled || from < 1)
        {
            SetBodyAutoHeight();
            return;
        }

        DispatcherQueue.TryEnqueue(() => AnimateFrom(from));
    }

    private void AnimateFrom(double from)
    {
        if (!_loaded) return;
        StopAnimation();
        BodyHost.Height = double.NaN;
        BodyHost.UpdateLayout();
        var to = BodyHost.ActualHeight;
        if (to < 1 || Math.Abs(to - from) < 1)
        {
            BodyHost.Height = double.NaN;
            UpdateClip();
            return;
        }

        _animating = true;
        BodyHost.Height = from;
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, BodyHost);
        Storyboard.SetTargetProperty(animation, "Height");
        _storyboard = new Storyboard();
        _storyboard.Children.Add(animation);
        _storyboard.Completed += (_, _) =>
        {
            _animating = false;
            BodyHost.Height = double.NaN;
            UpdateClip();
            _storyboard = null;
        };
        _storyboard.Begin();
    }

    private void SetBodyAutoHeight()
    {
        StopAnimation();
        BodyHost.Height = double.NaN;
        BodyHost.UpdateLayout();
        UpdateClip();
    }

    private void StopAnimation()
    {
        _storyboard?.Stop();
        _storyboard = null;
        _animating = false;
    }

    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) => UpdateClip();

    private void UpdateClip()
    {
        var width = BodyHost.ActualWidth;
        var height = BodyHost.ActualHeight;
        BodyClip.Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, width), Math.Max(0, height));
    }
}
