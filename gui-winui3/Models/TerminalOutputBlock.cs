using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace RuApp.Models;

/// <summary>
/// One terminal output block in the Chat tool-output panel: streamed text of
/// a single tool execution plus a best-effort title. Bounded so a runaway
/// command cannot grow UI memory without limit.
/// </summary>
public sealed class TerminalOutputBlock : INotifyPropertyChanged
{
    public const int MaximumLines = 200;
    public const int MaximumCharacters = 32 * 1024;

    private readonly StringBuilder _text = new();
    private int _lineCount;
    private bool _isTruncated;

    public TerminalOutputBlock(string executionId, string title)
    {
        ExecutionId = executionId;
        Title = title;
    }

    public string ExecutionId { get; }

    public string Title { get; }

    public string Text => _text.ToString();

    public bool IsCompleted { get; private set; }

    public bool IsFailed { get; private set; }

    public bool IsTruncated
    {
        get => _isTruncated;
        private set => SetProperty(ref _isTruncated, value);
    }

    public string StateText
    {
        get
        {
            if (IsFailed)
            {
                return "failed";
            }

            return IsCompleted ? "done" : "running";
        }
    }

    public void AppendText(string chunk)
    {
        if (string.IsNullOrEmpty(chunk) || IsCompleted)
        {
            return;
        }

        if (_text.Length >= MaximumCharacters || _lineCount >= MaximumLines)
        {
            IsTruncated = true;
            return;
        }

        var remaining = MaximumCharacters - _text.Length;
        var take = chunk.Length <= remaining ? chunk : chunk.Substring(0, remaining);
        _text.Append(take);
        _lineCount += CountLines(take);
        if (_text.Length >= MaximumCharacters || _lineCount >= MaximumLines)
        {
            IsTruncated = true;
        }

        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(FooterText));
    }

    public string FooterText => IsTruncated ? "… output truncated" : string.Empty;

    public void Complete(bool failed)
    {
        if (IsCompleted)
        {
            return;
        }

        IsCompleted = true;
        IsFailed = failed;
        OnPropertyChanged(nameof(StateText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static int CountLines(string value)
    {
        var count = 0;
        foreach (var ch in value)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
