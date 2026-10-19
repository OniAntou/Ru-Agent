using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuAgent.Client;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class SubagentTreeView
{
    private async void OnExportResult(object sender, RoutedEventArgs e)
    {
        if (_exporting || _disposed || _client is null || _model.Selected is not { } agent || _model.Result is not { } result) return;
        var root = _root;
        var binding = _binding;
        var client = _client;
        var ct = _lifetime.Token;
        _exporting = true;
        ExportButton.IsEnabled = false;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"{agent.AgentId}-result-{result.ResultVersion}" };
            picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance));
            var file = await picker.PickSaveFileAsync();
            ct.ThrowIfCancellationRequested();
            if (file is null) return;
            await Windows.Storage.FileIO.WriteTextAsync(file, "");
            int? next = 0;
            while (next is { } offset)
            {
                var detail = await client.GetSubagentAsync(root, agent.AgentId, result.ResultVersion, offset, ct);
                var page = detail.Result ?? throw new AgentApiException("subagent_result_missing", "Result page missing");
                ct.ThrowIfCancellationRequested();
                if (detail.RootRunId != root || detail.Agent.AgentId != agent.AgentId || page.ResultVersion != result.ResultVersion
                    || page.Offset != offset || page.NextOffset <= offset) throw new AgentApiException("invalid_response", "Invalid page cursor");
                await Windows.Storage.FileIO.AppendTextAsync(file, page.Text);
                next = page.NextOffset;
            }
            if (binding == _binding) ShowFeedback($"Exported result {result.ResultVersion}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception failure)
        {
            if (!ct.IsCancellationRequested && binding == _binding) ShowFeedback("Couldn't export the result. The saved file may be incomplete.");
            GuiDiagnosticWriter.AppendTraceLine($"[Subagents] Export failed: {failure.GetType().Name}");
        }
        finally
        {
            if (binding == _binding && !_disposed) { _exporting = false; RenderDetailHeader(); }
        }
    }
}
