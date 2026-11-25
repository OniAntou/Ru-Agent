using Microsoft.UI.Xaml;
using RuApp.Models;

namespace RuApp.Views;

/// <summary>
/// Reads a thinking row's whole reasoning when its block is opened. The row carries a
/// bounded excerpt; the Host names the stored reasoning when it had to cut, and the full
/// text is fetched once, through the run's payload route, only for a block someone opens.
/// </summary>
public sealed partial class ChatView
{
    /// <summary>Starts the read for an opened block whose row names more reasoning than it shows.</summary>
    private void LoadFullThinkingIfOpened(AgentActivityItem item)
    {
        if (!item.IsThinkingExpanded || _apiService is not { } api
            || item.ReasoningPayloadId is not { } payloadId
            || (item.OwnerRunId ?? _agentController?.CurrentRun?.RunId) is not { Length: > 0 } runId
            || !item.TryBeginFullThinkingLoad())
        {
            return;
        }

        _ = LoadFullThinkingAsync(api, item, runId, payloadId);
    }

    private async Task LoadFullThinkingAsync(Services.ApiService api, AgentActivityItem item, string runId, string payloadId)
    {
        string? text = null;
        try
        {
            text = await api.ReadPayloadTextAsync(runId, payloadId, CancellationToken.None);
        }
        catch (Exception error)
        {
            // The excerpt stays on screen and the block says the rest could not be read.
            Services.GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Full thinking read failed: {error.GetType().Name}");
        }

        if (DispatcherQueue.HasThreadAccess) item.CompleteFullThinkingLoad(text);
        else DispatcherQueue.TryEnqueue(() => item.CompleteFullThinkingLoad(text));
    }

    private void OnShowMoreThinkingClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AgentActivityItem item }) item.ShowMoreThinking();
    }
}
