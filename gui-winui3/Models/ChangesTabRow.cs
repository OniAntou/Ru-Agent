using System.ComponentModel;

namespace RuApp.Models;

/// <summary>
/// One row of the side panel's Changes tab: a projection of one edit activity,
/// with its own expand state so opening a diff in the panel never folds the
/// transcript's copy of the same edit.
/// </summary>
public sealed class ChangesTabRow : INotifyPropertyChanged
{
    public ChangesTabRow(
        string fileName,
        int added,
        int removed,
        string beforeText,
        string afterText,
        int? startLine,
        string safeCode)
    {
        FileName = fileName;
        Added = added;
        Removed = removed;
        BeforeText = beforeText;
        AfterText = afterText;
        StartLine = startLine;
        SafeCode = safeCode;
    }

    public string FileName { get; }

    public int Added { get; }

    public int Removed { get; }

    public string BeforeText { get; }

    public string AfterText { get; }

    public int? StartLine { get; }

    public string SafeCode { get; }

    public string AddedDeltaText => "+" + Added;

    public string RemovedDeltaText => "−" + Removed;

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpansionGlyph)));
        }
    }

    public string ExpansionGlyph => _isExpanded ? "\uE70D" : "\uE76C";

    public event PropertyChangedEventHandler? PropertyChanged;
}
