using System.ComponentModel;

namespace RuApp.Services;

/// <summary>Shared, bindable agent-feed density: roomy rows or compact ones.
/// The view owns the toggle and walks realized rows on change, so this is a
/// single place that answers "which density" and persists the answer.</summary>
public sealed class FeedDensity : INotifyPropertyChanged
{
    public static FeedDensity Instance { get; } = new();

    private readonly FeedDensityStore _store;
    private bool _isCompact;

    public FeedDensity(FeedDensityStore? store = null)
    {
        _store = store ?? new FeedDensityStore();
        _isCompact = _store.GetCompact();
    }

    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            if (_isCompact == value)
            {
                return;
            }

            _isCompact = value;
            _store.SetCompact(value);
            OnPropertyChanged(nameof(IsCompact));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
