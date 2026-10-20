namespace RuApp.Services;

/// <summary>Retains a local composer message across run-state projections.</summary>
internal sealed class ComposerLocalStatus
{
    internal string Text { get; private set; } = string.Empty;
    internal bool IsVisible => Text.Length > 0;

    internal void Set(string? status) => Text = status ?? string.Empty;
    internal void Clear() => Text = string.Empty;
}
