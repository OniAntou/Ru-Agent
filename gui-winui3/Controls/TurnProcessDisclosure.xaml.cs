using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RuApp.Services;
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
        new PropertyMetadata(string.Empty, OnHeaderTextChanged));

    public static readonly DependencyProperty HeaderMetaProperty = DependencyProperty.Register(
        nameof(HeaderMeta), typeof(string), typeof(TurnProcessDisclosure),
        new PropertyMetadata(string.Empty, OnHeaderChromeChanged));

    public static readonly DependencyProperty HeaderGlyphProperty = DependencyProperty.Register(
        nameof(HeaderGlyph), typeof(string), typeof(TurnProcessDisclosure),
        new PropertyMetadata(string.Empty, OnHeaderChromeChanged));

    public static readonly DependencyProperty HeaderIsFailedProperty = DependencyProperty.Register(
        nameof(HeaderIsFailed), typeof(bool), typeof(TurnProcessDisclosure),
        new PropertyMetadata(false, OnHeaderChromeChanged));

    public static readonly DependencyProperty HeaderIsWaitingProperty = DependencyProperty.Register(
        nameof(HeaderIsWaiting), typeof(bool), typeof(TurnProcessDisclosure),
        new PropertyMetadata(false, OnHeaderChromeChanged));

    /// <summary>
    /// True while <see cref="HeaderMeta"/> is the live thinking line: visual only, so the
    /// disclosure's accessible name and tooltip keep just the title and its action instead
    /// of re-reading a line that changes every paragraph.
    /// </summary>
    public static readonly DependencyProperty HeaderMetaIsTransientProperty = DependencyProperty.Register(
        nameof(HeaderMetaIsTransient), typeof(bool), typeof(TurnProcessDisclosure),
        new PropertyMetadata(false, OnHeaderChromeChanged));

    public static readonly DependencyProperty HeaderVisibleProperty = DependencyProperty.Register(
        nameof(HeaderVisible), typeof(bool), typeof(TurnProcessDisclosure),
        new PropertyMetadata(false, OnHeaderVisibleChanged));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(TurnProcessDisclosure),
        new PropertyMetadata(false, OnExpandedChanged));

    private bool _loaded;
    private bool _animating;
    private bool _pointerOver;
    private bool _focused;
    private double _pendingFrom;
    private Storyboard? _storyboard;
    private static readonly UISettings UiSettings = new();

    public TurnProcessDisclosure()
    {
        InitializeComponent();
        Typography.PropertyChanged += (_, _) => UpdateHeader();
        Loaded += (_, _) =>
        {
            _loaded = true;
            UpdateHeader();
            SetBodyAutoHeight();
        };
        Unloaded += (_, _) => StopAnimation();
    }

    public TranscriptTypography Typography => TranscriptTypography.Instance;

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

    public string HeaderMeta
    {
        get => (string)GetValue(HeaderMetaProperty);
        set => SetValue(HeaderMetaProperty, value);
    }

    public string HeaderGlyph
    {
        get => (string)GetValue(HeaderGlyphProperty);
        set => SetValue(HeaderGlyphProperty, value);
    }

    public bool HeaderIsFailed
    {
        get => (bool)GetValue(HeaderIsFailedProperty);
        set => SetValue(HeaderIsFailedProperty, value);
    }

    public bool HeaderIsWaiting
    {
        get => (bool)GetValue(HeaderIsWaitingProperty);
        set => SetValue(HeaderIsWaitingProperty, value);
    }

    public bool HeaderMetaIsTransient
    {
        get => (bool)GetValue(HeaderMetaIsTransientProperty);
        set => SetValue(HeaderMetaIsTransientProperty, value);
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

    private static void OnHeaderTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is TurnProcessDisclosure disclosure)
            disclosure.UpdateHeader();
    }

    private static void OnHeaderChromeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is TurnProcessDisclosure disclosure)
            disclosure.UpdateHeader();
    }

    private static void OnHeaderVisibleChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
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
        // Set explicitly as well as in XAML: the shared instance is the same source
        // the activity rows use, and an unresolved x:Bind must not leave the
        // disclosure one step smaller than the rows it folds.
        HeaderTextBlock.FontSize = Typography.ActivityFontSize;
        MetaText.FontSize = Typography.ActivityFontSize;
        var meta = string.IsNullOrEmpty(HeaderMeta) ? string.Empty : $"· {HeaderMeta}";
        // A live thinking line in the tail fades in when its paragraph changes, like the row's.
        if (HeaderMetaIsTransient && meta.Length > 0 && MetaText.Text.Length > 0
            && !string.Equals(meta, MetaText.Text, StringComparison.Ordinal) && UiTiming.AnimationsEnabled)
        {
            MetaText.Opacity = 0.25;
            UiMotion.FadeOpacity(MetaText, 1);
        }
        MetaText.Text = meta;
        MetaText.Visibility = string.IsNullOrEmpty(HeaderMeta) ? Visibility.Collapsed : Visibility.Visible;
        LeadingIcon.Glyph = string.IsNullOrEmpty(HeaderGlyph) ? "\uE945" : HeaderGlyph;
        ChevronIcon.Glyph = IsExpanded ? "\uE70E" : "\uE70D";
        ApplyLeadingState();
        ApplyTone();
        var action = IsExpanded ? "Hide turn process" : "Show turn process";
        // The recap tail can be trimmed in a narrow column; the tooltip and name keep it whole.
        // A live reasoning line is visual only: it changes every paragraph, so it stays out of
        // the accessible name a focused header would otherwise re-announce.
        var line = HeaderMetaIsTransient || string.IsNullOrEmpty(HeaderMeta)
            ? HeaderText
            : $"{HeaderText} · {HeaderMeta}";
        AutomationProperties.SetName(HeaderButton, $"{line}. {action}");
        AutomationProperties.SetItemStatus(HeaderButton, IsExpanded ? "Expanded" : "Collapsed");
        ToolTipService.SetToolTip(HeaderButton, $"{line}\n{action}");
    }

    /// <summary>The kind glyph gives way to the chevron on hover, focus or while open.</summary>
    private void ApplyLeadingState()
    {
        var chevron = IsExpanded || _pointerOver || _focused;
        LeadingIcon.Opacity = chevron ? 0 : 1;
        ChevronIcon.Opacity = chevron ? 1 : 0;
    }

    private void ApplyTone()
    {
        var key = HeaderIsFailed ? "RuDangerBrush"
            : HeaderIsWaiting ? "RuWarningBrush"
            : _pointerOver || _focused ? "RuInkBrush" : "RuMutedBrush";
        var brush = (Brush)Application.Current.Resources[key];
        LeadingIcon.Foreground = brush;
        ChevronIcon.Foreground = brush;
        // The activity row itself stays ink; only its meta tail is muted.
        HeaderTextBlock.Foreground = HeaderIsFailed || HeaderIsWaiting
            ? brush : (Brush)Application.Current.Resources["RuInkBrush"];
        MetaText.Foreground = HeaderIsFailed || HeaderIsWaiting
            ? brush : (Brush)Application.Current.Resources["RuMutedBrush"];
    }

    private void OnHeaderPointerEntered(object sender, PointerRoutedEventArgs e) =>
        SetHover(pointer: true, focus: _focused);

    private void OnHeaderPointerExited(object sender, PointerRoutedEventArgs e) =>
        SetHover(pointer: false, focus: _focused);

    private void OnHeaderGotFocus(object sender, RoutedEventArgs e) =>
        SetHover(pointer: _pointerOver, focus: HeaderButton.FocusState == FocusState.Keyboard);

    private void OnHeaderLostFocus(object sender, RoutedEventArgs e) =>
        SetHover(pointer: _pointerOver, focus: false);

    private void SetHover(bool pointer, bool focus)
    {
        _pointerOver = pointer;
        _focused = focus;
        ApplyLeadingState();
        ApplyTone();
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
