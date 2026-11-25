using System.ComponentModel;

namespace RuApp.Models;

public partial class ChatMessageItem
{
    private readonly HashSet<object> _observedRows = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Follows the rows the process header summarizes. A live row settles in place (the
    /// collection does not change), so without this the header kept naming a command
    /// that had already finished.
    /// </summary>
    private void ObserveProcessRows()
    {
        var rows = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (AgentActivities is { } activities) foreach (var row in activities) rows.Add(row);
        if (Segments is { } segments) foreach (var segment in segments.OfType<ActivitySegment>()) rows.Add(segment.Item);

        foreach (var gone in _observedRows.Where(row => !rows.Contains(row)).ToArray())
        {
            ((AgentActivityItem)gone).PropertyChanged -= OnProcessRowChanged;
            _observedRows.Remove(gone);
        }
        foreach (var row in rows)
            if (_observedRows.Add(row)) ((AgentActivityItem)row).PropertyChanged += OnProcessRowChanged;
    }

    private void OnProcessRowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or "" or nameof(AgentActivityItem.State) or nameof(AgentActivityItem.Detail)
            or nameof(AgentActivityItem.Count) or nameof(AgentActivityItem.FileName) or nameof(AgentActivityItem.Symbol)
            or nameof(AgentActivityItem.ToolDisplay) or nameof(AgentActivityItem.SafeCode))
            NotifyProcessSummaryChanged();
    }

    private void NotifyProcessSummaryChanged()
    {
        OnPropertyChanged(nameof(ProcessHeader));
        OnPropertyChanged(nameof(ProcessMeta));
        OnPropertyChanged(nameof(ProcessGlyph));
        OnPropertyChanged(nameof(ProcessIsFailed));
        OnPropertyChanged(nameof(ProcessIsWaiting));
    }
}
