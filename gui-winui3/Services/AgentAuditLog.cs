using System;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace RuApp.Services;

public sealed class AgentAuditLog : INotifyPropertyChanged, IDisposable
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private long _sequence;

    public ObservableCollection<AgentEventModel> Entries { get; } = new();

    public AgentAuditLog(int capacity = 500)
    {
        _capacity = capacity > 0 ? capacity : 500;
    }

    public long LastSequence
    {
        get { lock (_gate) return _sequence; }
    }

    public void Record(AgentEventModel model)
    {
        if (model is null)
        {
            return;
        }

        lock (_gate)
        {
            _sequence++;
            if (Entries.Count >= _capacity)
            {
                Entries.RemoveAt(0);
            }

            Entries.Add(model);
        }

        OnPropertyChanged(nameof(LastSequence));
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sequence = 0;
        }

        Entries.Clear();
        OnPropertyChanged(nameof(LastSequence));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
    }
}
