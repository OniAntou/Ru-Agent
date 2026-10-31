using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls;

/// <summary>
/// Base for the parts of an activity row's headline that sit around its verb label.
/// It follows the bound item and the transcript size, and releases both on unload.
/// </summary>
/// <remarks>
/// The verb itself stays a named TextBlock in the template: the running sweep and the
/// settled tone are applied to it by name (ChatView.ActivityLabelElementName). These
/// parts only add the glyph before it and the subject and numbers after it.
/// </remarks>
public abstract partial class ActivityRowPart : Grid
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(AgentActivityItem), typeof(ActivityRowPart),
        new PropertyMetadata(null, (sender, _) => ((ActivityRowPart)sender).Bind()));

    private AgentActivityItem? _observed;
    private bool _typographyObserved;

    protected ActivityRowPart()
    {
        VerticalAlignment = VerticalAlignment.Center;
        Loaded += (_, _) => Bind();
        Unloaded += (_, _) => Release();
    }

    public AgentActivityItem? Item
    {
        get => (AgentActivityItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    protected abstract void Render(AgentActivityItem item);

    protected static Brush Brush(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    protected static TextBlock Text(bool mono)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.NoWrap, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        if (Application.Current?.Resources.TryGetValue(mono ? "RuMonoFontFamily" : "RuUiFontFamily", out var font) == true
            && font is FontFamily family)
            text.FontFamily = family;
        return text;
    }

    /// <summary>Activity text follows the transcript size; mono figures sit a step under it.</summary>
    protected static double FontSizeFor(bool mono) =>
        TranscriptTypography.Instance.ActivityFontSize - (mono ? 1 : 0);

    private void Bind()
    {
        var item = IsLoaded ? Item : null;
        if (!ReferenceEquals(item, _observed))
        {
            if (_observed is not null) _observed.PropertyChanged -= OnItemChanged;
            _observed = item;
            if (item is not null) item.PropertyChanged += OnItemChanged;
        }

        if (IsLoaded && !_typographyObserved)
        {
            TranscriptTypography.Instance.PropertyChanged += OnTypographyChanged;
            _typographyObserved = true;
        }

        if (Item is { } current) Render(current);
    }

    private void Release()
    {
        if (_observed is not null) _observed.PropertyChanged -= OnItemChanged;
        _observed = null;
        if (_typographyObserved) TranscriptTypography.Instance.PropertyChanged -= OnTypographyChanged;
        _typographyObserved = false;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Item is { } item) Render(item);
    }

    private void OnTypographyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Item is { } item) Render(item);
    }
}

/// <summary>
/// The glyph before an activity row's verb: the kind of work, or the state when the
/// row failed or waits on the user. It is tinted, never animated; the verb's sweep
/// already says the row is running.
/// </summary>
public sealed partial class ActivityRowGlyph : ActivityRowPart
{
    private readonly FontIcon _glyph = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };

    public ActivityRowGlyph()
    {
        Width = 16;
        Height = 16;
        Children.Add(_glyph);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
            this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    protected override void Render(AgentActivityItem item)
    {
        var failed = item.IsFailed || item.HasFailingExitCode;
        ActivityRowGlyphs.Apply(_glyph,
            failed ? "\uE7BA" : item.IsWaiting ? "\uE823" : ActivityRowGlyphs.For(item.Kind, item.ToolDisplay),
            FontSizeFor(mono: false));
        _glyph.Foreground = Brush(failed ? "RuDangerBrush"
            : item.IsWaiting ? "RuWarningBrush"
            : item.IsRunning ? "RuAccentBrush" : "RuMutedBrush");
    }
}

/// <summary>
/// What follows an activity row's verb: a dot, the subject (file, command, query,
/// host, tool), then a mono tail of numbers, the line delta and a failing exit code.
/// Every part sits right after the one before it; only the subject gives way.
/// </summary>
public sealed partial class ActivityRowTail : ActivityRowPart
{
    private readonly Border _dot = new()
    {
        Width = 3, Height = 3, CornerRadius = UiCornerRadius.MicroPill, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _subject = Text(mono: false);
    private readonly TextBlock _meta = Text(mono: true);
    private readonly TextBlock _added = Text(mono: true);
    private readonly TextBlock _removed = Text(mono: true);
    private readonly Border _exit = new()
    {
        Padding = new Thickness(6, 0, 6, 1), BorderThickness = new Thickness(1),
        CornerRadius = UiCornerRadius.Inset, VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _exitText = Text(mono: true);

    public ActivityRowTail()
    {
        ColumnSpacing = 8;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        for (var i = 0; i < 5; i++) ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _subject.TextTrimming = TextTrimming.CharacterEllipsis;
        Place(_dot, 0);
        Place(_subject, 1);
        Place(_meta, 2);
        var delta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        delta.Children.Add(_added);
        delta.Children.Add(_removed);
        Place(delta, 3);
        _exit.Child = _exitText;
        Place(_exit, 4);
        SizeChanged += (_, _) => FitSubject();
    }

    protected override void Render(AgentActivityItem item)
    {
        var subject = item.RowSubject;
        _subject.Text = subject;
        _subject.Visibility = subject.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _subject.Foreground = Brush("RuMutedBrush");
        _subject.FontSize = FontSizeFor(mono: false);
        ToolTipService.SetToolTip(_subject, subject.Length > 0 ? subject : null);

        var meta = item.RowMeta;
        _meta.Text = meta;
        _meta.Visibility = meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _meta.Foreground = Brush("RuMutedBrush");
        _meta.FontSize = FontSizeFor(mono: true);
        _dot.Background = Brush("RuMutedBrush");
        _dot.Visibility = subject.Length > 0 || meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var delta = item.HasLineDelta;
        _added.Text = delta ? item.AddedDeltaText : string.Empty;
        _removed.Text = delta ? item.RemovedDeltaText : string.Empty;
        _added.Visibility = delta ? Visibility.Visible : Visibility.Collapsed;
        _removed.Visibility = _added.Visibility;
        _added.Foreground = Brush("RuSuccessBrush");
        _removed.Foreground = Brush("RuDangerBrush");
        _added.FontSize = _removed.FontSize = FontSizeFor(mono: true);

        _exitText.Text = item.ExitCodeText;
        _exitText.Foreground = Brush("RuDangerBrush");
        _exitText.FontSize = FontSizeFor(mono: true) - 1;
        _exit.BorderBrush = Brush("RuDangerBrush");
        _exit.Visibility = item.HasFailingExitCode ? Visibility.Visible : Visibility.Collapsed;
        FitSubject();
    }

    // The subject takes whatever width the other parts leave, so a long command or
    // path trims instead of pushing the numbers off the row.
    private void FitSubject()
    {
        if (ActualWidth <= 0) return;
        double used = 0;
        var visible = 0;
        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Visible) continue;
            visible++;
            if (ReferenceEquals(child, _subject)) continue;
            child.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            used += child.DesiredSize.Width;
        }

        _subject.MaxWidth = Math.Max(40, ActualWidth - used - ColumnSpacing * Math.Max(0, visible - 1));
    }

    private void Place(FrameworkElement element, int column)
    {
        SetColumn(element, column);
        Children.Add(element);
    }
}

/// <summary>Segoe Fluent glyphs for each kind of activity row.</summary>
public static class ActivityRowGlyphs
{
    public const string TerminalPrompt = ">_";

    /// <summary>Whether the glyph is plain text to draw in the mono font, not a symbol.</summary>
    public static bool IsText(string glyph) => glyph == TerminalPrompt;

    /// <summary>Points a glyph element at the right font for <paramref name="glyph"/>.</summary>
    public static void Apply(FontIcon icon, string glyph, double size)
    {
        icon.Glyph = glyph;
        var key = IsText(glyph) ? "RuMonoFontFamily" : "SymbolThemeFontFamily";
        if (Application.Current?.Resources.TryGetValue(key, out var font) == true && font is FontFamily family)
            icon.FontFamily = family;
        // Two mono characters read larger than one symbol at the same size.
        icon.FontSize = IsText(glyph) ? size - 3 : size;
        icon.FontWeight = IsText(glyph) ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    }

    public static string For(AgentActivityKind kind, string? toolDisplay) => kind switch
    {
        AgentActivityKind.Thinking => "\uE82F",
        AgentActivityKind.Explore => "\uE8B7",
        AgentActivityKind.Search => "\uE721",
        AgentActivityKind.Analyze => toolDisplay == "Docs" ? "\uE82D" : "\uE8A5",
        AgentActivityKind.Edit => toolDisplay == "Delete" ? "\uE74D" : "\uE70F",
        // The prompt itself, drawn as text (see IsText): the boxed console glyph was the
        // heaviest mark on the row, and the symbol font has no unboxed ">_".
        AgentActivityKind.Terminal => TerminalPrompt,
        AgentActivityKind.Web => "\uE774",
        AgentActivityKind.Mcp => "\uE71B",
        AgentActivityKind.Computer => "\uE7F4",
        AgentActivityKind.Skill => "\uE945",
        AgentActivityKind.Program => "\uE943",
        AgentActivityKind.Todo => "\uE9D5",
        AgentActivityKind.Compaction => "\uE73F",
        AgentActivityKind.Retry => "\uE72C",
        _ => "\uE946",
    };
}
