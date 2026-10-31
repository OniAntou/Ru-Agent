using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Controls;

public sealed record TurnChangesRequestedEventArgs(ChatMessageItem Message, string? FilePath);

public sealed partial class TurnChangesSummaryControl
{
    public event EventHandler<TurnChangesRequestedEventArgs>? DetailsRequested;

    private void OnViewChangesClick(object sender, RoutedEventArgs args) => RequestDetails(null);

    private void OnFileClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: TurnChangedFile file }) RequestDetails(file.Path);
    }

    private void RequestDetails(string? path)
    {
        if (IsLoaded && Message is { IsStreaming: false } owner && _summary.Files.Count > 0)
            DetailsRequested?.Invoke(this, new(owner, path));
    }
}
