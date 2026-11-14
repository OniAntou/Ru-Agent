using System.ComponentModel;

namespace RuApp.Services;

/// <summary>
/// The user-draggable width of the conversation column, ported from DSH's
/// ConversationWidthControls. It is intentionally session-local: a restart
/// starts at the default width, and a narrower window clamps the rendered width
/// without losing the reader's preferred width for when the window grows again.
/// </summary>
public sealed class ConversationWidthState : INotifyPropertyChanged
{
    /// <summary>The single width shared by the transcript handles, messages and composer.</summary>
    public static ConversationWidthState Instance { get; } = new();

    public const double MinimumWidth = 640;
    public const double MaximumWidth = 920;
    public const double DefaultWidth = 720;
    /// <summary>Room left for the two transparent grab strips and their margin.</summary>
    public const double EdgeBudget = 80;
    /// <summary>Width of the transparent grab strip outside each content edge.</summary>
    public const double HandleOutset = 32;
    /// <summary>The composer keeps a 16px gutter on both sides of the transcript.</summary>
    public const double ComposerGutter = 32;

    private double _viewportWidth = 1200;
    private double _preferredWidth = DefaultWidth;
    private double _dragStartWidth = DefaultWidth;
    private double _contentWidth = DefaultWidth;

    public ConversationWidthState()
    {
        Recompute();
    }

    public double ContentWidth
    {
        get => _contentWidth;
        private set
        {
            var clamped = Math.Max(MinimumWidth, Math.Round(value));
            if (Math.Abs(clamped - _contentWidth) < 0.5) return;
            _contentWidth = clamped;
            OnPropertyChanged(nameof(ContentWidth));
            OnPropertyChanged(nameof(HandleLayerWidth));
            OnPropertyChanged(nameof(ComposerWidth));
        }
    }

    /// <summary>The readable column, bounded by the window and the hard 920px cap.</summary>
    public double HandleLayerWidth => ContentWidth + HandleOutset * 2;

    /// <summary>The composer and approval overlay follow the transcript width.</summary>
    public double ComposerWidth => ContentWidth + ComposerGutter;

    public double ViewportWidth => _viewportWidth;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Updates the available width; a later grow restores the preferred width.</summary>
    public void SetViewportWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0) return;
        _viewportWidth = width;
        Recompute();
    }

    /// <summary>Captures the preferred width before a pointer drag starts.</summary>
    public void BeginDrag() => _dragStartWidth = _preferredWidth;

    /// <summary>Live pointer preview; the effective width is clamped to the window.</summary>
    public void PreviewWidth(double width)
    {
        if (!double.IsFinite(width)) return;
        _preferredWidth = Math.Clamp(width, MinimumWidth, MaximumWidth);
        Recompute();
    }

    /// <summary>Restores the width from before the current drag.</summary>
    public void CancelDrag()
    {
        _preferredWidth = _dragStartWidth;
        Recompute();
    }

    private void Recompute()
    {
        var viewportMaximum = Math.Max(MinimumWidth, _viewportWidth - EdgeBudget);
        var effective = Math.Min(_preferredWidth, Math.Min(MaximumWidth, viewportMaximum));
        ContentWidth = Math.Max(MinimumWidth, effective);
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
