using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuAgent.Client;
using RuAgent.Protocol.Subagents;
using RuApp.Services;

namespace RuApp.Views;

/// <summary>One root-bound observer supplies the summary card, grouped list and detail reader.</summary>
public sealed partial class SubagentTreeView : UserControl, IDisposable
{
    private readonly SubagentTreeModel _model = new();
    private readonly DispatcherQueueTimer _timer;
    private CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _detailRead;
    private ISubagentClient? _client;
    private string _root = "";
    private bool _refreshing;
    private bool _refreshRequested;
    private bool _refreshFailed;
    private bool _disposed;
    private long _binding;
    public ObservableCollection<SubagentRow> Working { get; } = [];
    public ObservableCollection<SubagentRow> Attention { get; } = [];
    public ObservableCollection<SubagentRow> Finished { get; } = [];
    public SubagentListResponse? Snapshot => _model.Snapshot;
    public bool RefreshFailed => _refreshFailed;
    public event EventHandler? SnapshotChanged;
    public event EventHandler? CloseRequested;

    public SubagentTreeView()
    {
        InitializeComponent();
        ChildTranscript.ItemsSource = _transcript.Messages;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = SubagentPresentation.ActiveRefresh;
        _timer.Tick += OnTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Bind(ISubagentClient? client, string? root)
    {
        if (_disposed) return;
        root ??= "";
        if (_root == root && ReferenceEquals(_client, client)) return;
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new();
        _binding++;
        _root = root;
        _client = client;
        _model.Bind(root, reset: true);
        ResetDetailRead();
        ClearChildActivity();
        _exporting = false;
        _refreshFailed = false;
        ClearResult();
        ShowFeedback("");
        RenderSnapshot();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
        if (client is not null && root.Length > 0)
        {
            _timer.Interval = SubagentPresentation.ActiveRefresh;
            _timer.Start();
            _ = RefreshAsync();
        }
        else _timer.Stop();
    }

    /// <summary>
    /// The root's transient <c>subagent.changed</c> hint: read the durable snapshot now
    /// instead of waiting for the next tick. The hint itself carries no trusted state.
    /// </summary>
    public void NotifyChanged(string? rootRunId)
    {
        if (_disposed || _client is null || _root.Length == 0 || rootRunId != _root || _lifetime.IsCancellationRequested) return;
        if (_refreshing) { _refreshRequested = true; return; }
        _ = RefreshAsync();
    }

    /// <summary>Poll fast while work is live, slowly while the root may still delegate, not at all once settled.</summary>
    private void ApplyRefreshCadence()
    {
        if (_disposed || _lifetime.IsCancellationRequested) return;
        if (SubagentPresentation.RefreshInterval(_model.Snapshot) is not { } interval) { _timer.Stop(); return; }
        if (_timer.Interval != interval) _timer.Interval = interval;
        if (!_timer.IsRunning) _timer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        if (_lifetime.IsCancellationRequested) { _lifetime.Dispose(); _lifetime = new(); ResetDetailRead(); }
        if (_client is not null && _root.Length > 0) { _timer.Start(); _ = RefreshAsync(); }
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        _timer.Stop();
        _lifetime.Cancel();
    }
    private async void OnTick(DispatcherQueueTimer sender, object args) => await RefreshAsync();
    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_disposed || _refreshing || _client is null || _root.Length == 0 || _lifetime.IsCancellationRequested) return;
        _refreshing = true;
        var ct = _lifetime.Token;
        var ticket = _model.BeginRead();
        var priorVersion = _model.SelectedVersion;
        try
        {
            var snapshot = await _client.ListSubagentsAsync(_root, ct);
            ct.ThrowIfCancellationRequested();
            if (_refreshFailed && snapshot.RootRunId == ticket.Root)
            {
                _refreshFailed = false;
                ShowFeedback("");
                RenderSnapshot();
                SnapshotChanged?.Invoke(this, EventArgs.Empty);
            }
            if (!_model.Apply(ticket, snapshot))
            {
                // Same snapshot: only a running attempt's elapsed time moved.
                if (Working.Count > 0) { RenderSnapshot(); RenderDetailStatus(); }
                // The open child's own replay moves between snapshot changes while it works.
                if (_model.SelectedAgentId is not null) await RefreshChildActivityAsync();
                return;
            }
            ShowFeedback("");
            RenderSnapshot();
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            if (_model.SelectedAgentId is null) { ResetDetailRead(); ClearResult(); }
            else
            {
                RenderDetailHeader();
                if (_model.Result is null) { ResetDetailRead(); ClearResult(); }
                await RefreshChildActivityAsync();
                if (priorVersion != _model.SelectedVersion) { ResetDetailRead(); ClearResult(); await ReadPageAsync(0); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (AgentApiException) { if (!ct.IsCancellationRequested) ReportRefreshFailure(); }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested) ReportRefreshFailure();
            GuiDiagnosticWriter.AppendTraceLine($"[Subagents] Refresh failed: {e.GetType().Name}");
        }
        finally
        {
            _refreshing = false;
            ApplyRefreshCadence();
            if (_refreshRequested)
            {
                _refreshRequested = false;
                _ = RefreshAsync();
            }
        }
    }

    private void ReportRefreshFailure()
    {
        bool changed = !_refreshFailed;
        _refreshFailed = true;
        RenderSnapshot();
        ShowFeedback("Couldn't refresh tasks. Use Refresh to try again.");
        if (changed) SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderSnapshot()
    {
        var snapshot = _model.Snapshot;
        var now = DateTimeOffset.UtcNow;
        var rows = snapshot?.Agents.Select(agent => SubagentPresentation.Row(agent, now)).ToArray() ?? [];
        Reconcile(Working, rows.Where(r => r.Group == SubagentGroup.Working).ToArray());
        Reconcile(Attention, rows.Where(r => r.Group == SubagentGroup.Attention).ToArray());
        Reconcile(Finished, rows.Where(r => r.Group == SubagentGroup.Finished).ToArray());
        WorkingHeading.Text = $"Working · {Working.Count}";
        AttentionHeading.Text = $"Needs attention · {Attention.Count}";
        FinishedHeading.Text = $"Finished · {Finished.Count}";
        WorkingEmpty.Visibility = Working.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AttentionGroup.Visibility = Attention.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FinishedGroup.Visibility = Finished.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskGroups.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var loading = snapshot is null && _client is not null && _root.Length > 0;
        EmptyTitle.Text = loading ? _refreshFailed ? "Tasks couldn't be loaded" : "Loading delegated work…" : "No delegated work yet";
        EmptyDescription.Text = loading ? _refreshFailed ? "Refresh to try again." : "Fetching this run's tasks." : "Coding and review tasks appear here when Ru delegates work.";
        SnapshotSummary.Text = SubagentPresentation.ObserverSummary(snapshot, _refreshFailed);
        var preparation = snapshot?.Preparations.Count ?? 0;
        var unavailable = snapshot?.UnavailableAttempts ?? 0;
        PreparationStatus.Text = (preparation > 0 ? $"Preparing {preparation} task(s). " : "")
            + (unavailable > 0 ? $"{unavailable} attempt(s) couldn't be loaded." : "");
        PreparationStatus.Visibility = PreparationStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var diagnostics = snapshot?.ProfileDiagnostics is { Count: > 0 } issues
            ? "Subagent profiles need attention:\n" + string.Join("\n", issues.Take(8).Select(issue => "• " + issue))
            : "";
        ProfileIssues.Text = diagnostics;
        ProfileIssues.Visibility = diagnostics.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ListBody.Visibility = _model.SelectedAgentId is null ? Visibility.Visible : Visibility.Collapsed;
        DetailBody.Visibility = _model.SelectedAgentId is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void Reconcile(ObservableCollection<SubagentRow> target, SubagentRow[] rows)
    {
        for (var i = 0; i < rows.Length; i++)
        {
            var existing = target.Select((row, index) => (row, index)).FirstOrDefault(p => p.row.AgentId == rows[i].AgentId);
            if (existing.row is null) target.Insert(i, rows[i]);
            else
            {
                if (existing.index != i) target.Move(existing.index, i);
                if (target[i].SameState(rows[i])) target[i].Meta = rows[i].Meta;
                else target[i] = rows[i];
            }
        }
        while (target.Count > rows.Length) target.RemoveAt(target.Count - 1);
    }

    private void ShowFeedback(string message)
    {
        Feedback.Text = message;
        Feedback.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _client = null;
        _detailRead?.Cancel();
        _detailRead?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
