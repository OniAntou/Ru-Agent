using Microsoft.UI.Xaml;
using System.ComponentModel;

namespace RuApp.Models;

/// <summary>
/// One row of the side panel's Files tab: a flat tree row (children are
/// inserted after their parent with a deeper indent) or a placeholder note for
/// a bounded result.
/// </summary>
public sealed class FileTreeRow : INotifyPropertyChanged
{
    private const double IndentPerDepth = 14;

    public FileTreeRow(string name, string fullPath, bool isDirectory, bool canExpand, int depth)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        CanExpand = canExpand;
        Depth = depth;
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public bool CanExpand { get; }

    public int Depth { get; }

    public bool IsNote { get; private init; }

    /// <summary>Whether the row's children are currently listed under it.</summary>
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            ExpansionVersion++;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new(nameof(ExpansionGlyph)));
        }
    }
    internal long ExpansionVersion { get; private set; }

    public string ExpansionGlyph => !CanExpand ? string.Empty : IsExpanded ? "\uE70D" : "\uE76C";
    public event PropertyChangedEventHandler? PropertyChanged;

    public Thickness IndentMargin => new(Depth * IndentPerDepth, 0, 0, 0);

    public string IconGlyph => IsNote
        ? string.Empty
        : IsDirectory ? "\uE8B7" : "\uE7C3";

    public Visibility NoteVisibility => IsNote ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NameVisibility => IsNote ? Visibility.Collapsed : Visibility.Visible;

    public static FileTreeRow Note(string text, int depth) =>
        new(text, string.Empty, isDirectory: false, canExpand: false, depth) { IsNote = true };
}
