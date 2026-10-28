using System.ComponentModel;

namespace RuApp.Services;

public sealed class AgentActivityDisplayPreferences : INotifyPropertyChanged
{
    private static readonly Lazy<AgentActivityDisplayPreferences> Shared = new(() => new());
    public static AgentActivityDisplayPreferences Instance => Shared.Value;
    private readonly AgentActivityDisplayPreferenceStore _store;
    private bool _compact;

    public AgentActivityDisplayPreferences(AgentActivityDisplayPreferenceStore? store = null)
    {
        _store = store ?? new();
        _compact = _store.LoadCompact();
    }

    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            _store.TrySaveCompact(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Compact)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
