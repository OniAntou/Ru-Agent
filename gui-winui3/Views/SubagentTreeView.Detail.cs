using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RuAgent.Client;
using RuApp.Services;

namespace RuApp.Views;

public sealed partial class SubagentTreeView
{
    private sealed record ResultChoice(long Version) { public string Label => $"Result {Version}"; }
    private bool _changingVersions;
    private bool _reading;
    private bool _exporting;
    private long _readOperation;
    private int? _nextOffset;
    private string _rendered = "";
    private int _previewBytes;

    public void ShowList()
    {
        if (_disposed) return;
        _model.ShowList();
        ResetDetailRead();
        ClearResult();
        ShowFeedback("");
        RenderSnapshot();
        (Working.Count > 0 ? WorkingList : Attention.Count > 0 ? AttentionList : FinishedList).Focus(FocusState.Programmatic);
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowList();
    private void OnBrowserKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        if (_model.SelectedAgentId is not null) ShowList();
        else CloseRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
    private async void OnTaskClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SubagentRow row || !_model.SelectAgent(row.AgentId)) return;
        ResetDetailRead();
        ClearResult();
        ShowFeedback("");
        RenderSnapshot();
        RenderDetailHeader();
        DetailScroll.ChangeView(null, 0, null, disableAnimation: true);
        BackButton.Focus(FocusState.Programmatic);
        await ReadPageAsync(0);
    }

    private void ResetDetailRead()
    {
        _detailRead?.Cancel();
        _detailRead?.Dispose();
        _detailRead = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _readOperation++;
        _reading = false;
    }
    private void ClearResult()
    {
        _rendered = "";
        _previewBytes = 0;
        _nextOffset = null;
        ResultBody.MarkdownText = "";
        ResultLoading.Visibility = Visibility.Collapsed;
        MoreButton.Visibility = PreviewNotice.Visibility = Visibility.Collapsed;
    }

    private void RenderDetailHeader()
    {
        if (_model.Selected is not { } agent) return;
        var row = SubagentPresentation.Row(agent);
        var result = _model.Result;
        var attempt = agent.Attempts.SingleOrDefault(a => a.RunId == result?.RunId) ?? agent.Attempts.LastOrDefault();
        SelectedTitle.Text = row.Title;
        ToolTipService.SetToolTip(SelectedTitle, agent.Task ?? row.Title);
        SelectedGlyph.AgentId = agent.AgentId;
        SelectedGlyph.Role = agent.Role;
        SelectedStatus.Text = row.RoleLabel + " · " + row.Status;
        SelectedStatus.Foreground = (Brush)new SubagentStateBrushConverter().Convert(row.Tone, typeof(Brush), null!, "");
        SelectedModel.Text = attempt is null ? "Model unavailable" : $"{attempt.ProviderId ?? "Provider unavailable"} · {attempt.Model ?? "Model unavailable"}";
        ToolTipService.SetToolTip(SelectedModel, SelectedModel.Text);
        TaskBrief.Text = agent.Task ?? "No task brief is available.";
        var choices = agent.Results.Select(r => new ResultChoice(r.ResultVersion)).ToArray();
        _changingVersions = true;
        try
        {
            if (!ResultVersions.Items.Cast<ResultChoice>().Select(c => c.Version).SequenceEqual(choices.Select(c => c.Version)))
                ResultVersions.ItemsSource = choices;
            ResultVersions.SelectedItem = ResultVersions.Items.Cast<ResultChoice>().SingleOrDefault(c => c.Version == _model.SelectedVersion);
            ResultVersions.IsEnabled = choices.Length > 1;
            ResultVersions.Visibility = choices.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            SingleResultLabel.Text = result is null ? "" : $"Result {result.ResultVersion}";
            SingleResultLabel.Visibility = choices.Length > 1 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _changingVersions = false; }
        ExportButton.IsEnabled = result is not null && !_exporting;
        PendingResult.Visibility = result is null ? Visibility.Visible : Visibility.Collapsed;
        PendingResult.Text = row.Group == SubagentGroup.Working ? "Working on this task. The result will appear here."
            : "No result was published for this attempt.";
        ResultStatus.Text = result is null ? "" : result.ApplyStatus switch
        {
            "applied" => "Integrated into the workspace",
            "partial" => "Partly integrated · main agent follow-up needed",
            "unknown" or "pending" => "Integration needs main agent review",
            "conflict" => "Workspace changed · integration conflict",
            _ => agent.Role == "coding" && result.Status == "completed" && result.Code is null ? "Result ready · not integrated" : $"Result {result.ResultVersion} · {result.Status}",
        };
        ResultStatus.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
        var viewed = agent.Attempts.SingleOrDefault(a => a.RunId == result?.RunId) ?? attempt;
        RunDetails.Text = $"{SubagentPresentation.Usage(viewed?.Usage)}\n"
            + $"{viewed?.Protocol ?? "Protocol unavailable"} · attempt {viewed?.AttemptNumber.ToString() ?? "unavailable"}"
            + (result?.Code is { } code ? $"\nOutcome: {code}" : "")
            + $"\n{agent.AgentId}";
    }

    private async void OnResultVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingVersions || ResultVersions.SelectedItem is not ResultChoice choice || !_model.SelectVersion(choice.Version)) return;
        ResetDetailRead();
        ClearResult();
        RenderDetailHeader();
        await ReadPageAsync(0);
    }
    private async void OnNextPage(object sender, RoutedEventArgs e) { if (_nextOffset is { } next) await ReadPageAsync(next); }

    private async Task ReadPageAsync(int offset)
    {
        if (_disposed || _reading || _client is null || _model.Result is null || _detailRead is null) return;
        var ticket = _model.BeginResultRead();
        var ct = _detailRead.Token;
        var client = _client;
        var operation = ++_readOperation;
        _reading = true;
        MoreButton.IsEnabled = false;
        ResultLoading.Visibility = offset == 0 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var detail = await client.GetSubagentAsync(ticket.Root, ticket.AgentId, ticket.Version, offset, ct);
            ct.ThrowIfCancellationRequested();
            if (!_model.IsCurrent(ticket)) return;
            var page = detail.Result;
            if (detail.RootRunId != ticket.Root || detail.Agent.AgentId != ticket.AgentId || page is null
                || page.ResultVersion != ticket.Version || page.Offset != offset || page.NextOffset <= offset)
                throw new AgentApiException("invalid_response", "Invalid result page");
            var bytes = Encoding.UTF8.GetByteCount(page.Text);
            if (_previewBytes + bytes > SubagentPresentation.MaxPreviewBytes)
            {
                PreviewNotice.Text = "Preview limit reached. Export to read the full result.";
                PreviewNotice.Visibility = Visibility.Visible;
                MoreButton.Visibility = Visibility.Collapsed;
                return;
            }
            _rendered = offset == 0 ? page.Text : _rendered + page.Text;
            _previewBytes = offset == 0 ? bytes : _previewBytes + bytes;
            ResultBody.MarkdownText = _rendered;
            _nextOffset = page.NextOffset;
            MoreButton.Content = "Read more";
            MoreButton.Visibility = _nextOffset is null ? Visibility.Collapsed : Visibility.Visible;
            ShowFeedback("");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested && _model.IsCurrent(ticket))
            {
                ShowFeedback("Couldn't load the result. Try again.");
                _nextOffset = offset;
                MoreButton.Content = "Try again";
                MoreButton.Visibility = Visibility.Visible;
            }
            GuiDiagnosticWriter.AppendTraceLine($"[Subagents] Result read failed: {e.GetType().Name}");
        }
        finally
        {
            if (operation == _readOperation)
            {
                _reading = false;
                MoreButton.IsEnabled = true;
                ResultLoading.Visibility = Visibility.Collapsed;
            }
        }
    }
}
