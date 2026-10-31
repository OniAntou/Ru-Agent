using System.ComponentModel;

namespace RuApp.Services;

/// <summary>Shared, bindable conversation typography: message size 14-17 (default 14),
/// activity lines the same size. XAML binds via the registered instance.</summary>
public sealed class TranscriptTypography : INotifyPropertyChanged
{
    public static TranscriptTypography Instance { get; } = new();

    private readonly TranscriptFontSizeStore _store;
    private double _messageFontSize;

    public TranscriptTypography(TranscriptFontSizeStore? store = null)
    {
        _store = store ?? new TranscriptFontSizeStore();
        _messageFontSize = _store.GetSize();
    }

    public double MessageFontSize
    {
        get => _messageFontSize;
        set
        {
            var clamped = TranscriptFontSizeStore.Clamp(value);
            if (Math.Abs(clamped - _messageFontSize) < 0.001)
            {
                return;
            }

            _messageFontSize = clamped;
            _store.SetSize(clamped);
            OnPropertyChanged(nameof(MessageFontSize));
            OnPropertyChanged(nameof(ActivityFontSize));
        }
    }

    /// <summary>Activity rows read at the reply's own size; their muted ink sets them apart.</summary>
    public double ActivityFontSize => _messageFontSize;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
