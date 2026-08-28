using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using RuAgent.Client;
using RuAgent.Protocol.Harness;

namespace RuApp.Services;

public sealed class AgentRunController : INotifyPropertyChanged, IDisposable
{
    private readonly IAgentRunClient _client;
    private readonly HarnessEventSink? _sink;
    private readonly Action<Action>? _synchronize;
    private readonly Action<AgentEventModel>? _auditSink;
    private readonly object _gate = new();
    private readonly HashSet<string> _assistantDeltaKeys = new(StringComparer.Ordinal);

    private AgentRunModel? _currentRun;
    private string _statusText = "Sẵn sàng";
    private bool _isBusy;
    private string? _currentRunId;
    private string? _waitKind;
    private string? _pendingApprovalId;
    private string? _pendingApprovalSummary;
    private bool _isCancelRequested;
    private CancellationTokenSource? _subscriptionCts;

    public AgentRunController(IAgentRunClient client, HarnessEventSink? sink = null, Action<Action>? synchronize = null, Action<AgentEventModel>? auditSink = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sink = sink;
        _synchronize = synchronize;
        _auditSink = auditSink;
    }

    public ObservableCollection<AgentEventModel> Events { get; } = new();

    public AgentRunModel? CurrentRun
    {
        get { lock (_gate) return _currentRun; }
        private set
        {
            lock (_gate)
            {
                _currentRun = value;
            }

            OnPropertyChanged(nameof(CurrentRun));
            OnPropertyChanged(nameof(NeedsWorkspaceTrust));
        }
    }

    public string StatusText
    {
        get { lock (_gate) return _statusText; }
        private set { lock (_gate) { _statusText = value; } OnPropertyChanged(nameof(StatusText)); }
    }

    public bool IsBusy
    {
        get { lock (_gate) return _isBusy; }
        private set { lock (_gate) { _isBusy = value; } OnPropertyChanged(nameof(IsBusy)); }
    }

    public string? PendingApprovalId
    {
        get { lock (_gate) return _pendingApprovalId; }
        private set
        {
            lock (_gate)
            {
                _pendingApprovalId = value;
            }

            OnPropertyChanged(nameof(PendingApprovalId));
            OnPropertyChanged(nameof(HasPendingApproval));
        }
    }

    public string? PendingApprovalSummary
    {
        get { lock (_gate) return _pendingApprovalSummary; }
        private set
        {
            lock (_gate)
            {
                _pendingApprovalSummary = value;
            }

            OnPropertyChanged(nameof(PendingApprovalSummary));
        }
    }

    public bool HasPendingApproval => PendingApprovalId is not null;

    public bool NeedsWorkspaceTrust =>
        CurrentRun?.Status == "waiting_workspace_trust"
        && !string.IsNullOrWhiteSpace(CurrentRun.TrustRequestId)
        && !string.IsNullOrWhiteSpace(CurrentRun.DisplayIdentityHash);
    public bool CanCancel
    {
        get { lock (_gate) return _currentRunId is not null && !_isCancelRequested; }
    }

    public bool CanContinue
    {
        get { lock (_gate) return _currentRunId is not null && _waitKind == "waiting_user" && !_isCancelRequested; }
    }

    public async Task CreateAndRunAsync(CreateAgentRunRequest request, CancellationToken externalCancellation = default)
    {
        bool alreadyBusy;
        lock (_gate)
        {
            alreadyBusy = _isBusy;
            if (!alreadyBusy)
            {
                _isBusy = true;
            }
        }

        if (alreadyBusy)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
        lock (_gate)
        {
            _subscriptionCts = cts;
        }

        var token = cts.Token;

        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(IsBusy));
            _isCancelRequested = false;
            _waitKind = null;
            PendingApprovalId = null;
            PendingApprovalSummary = null;
            Events.Clear();
            lock (_gate)
            {
                _assistantDeltaKeys.Clear();
            }
            RefreshCommands();
        });

        try
        {
            var run = await _client.CreateRunAsync(request, token).ConfigureAwait(false);
            RunOnUi(() =>
            {
                CurrentRun = run;
                _currentRunId = run.RunId;
                _waitKind = run.HistoryWaitKind;
                StatusText = $"Đang chạy: {run.RunId}";
                RefreshCommands();
            });

            await _client.SubscribeEventsAsync(
                    run.RunId,
                    0,
                    OnEventAsync,
                    token,
                    OnActivityAsync,
                    OnSummaryAsync,
                    OnAssistantDeltaAsync)
                .ConfigureAwait(false);
            // The SSE stream ends after the terminal snapshot, but the create response
            // may be stale (or the stream may have disconnected). Refresh the durable
            // run before changing command state so Cancel is never left enabled for a
            // completed run and a disconnect is not reported as success.
            await RefreshAsync(token).ConfigureAwait(false);

            RunOnUi(() =>
            {
                var terminal = IsTerminalStatus(CurrentRun?.Status);
                if (terminal)
                {
                    _currentRunId = null;
                    _waitKind = null;
                    PendingApprovalId = null;
                    PendingApprovalSummary = null;
                    if (!_isCancelRequested)
                    {
                        StatusText = "Phiên chạy đã kết thúc.";
                    }
                }
                else if (!_isCancelRequested)
                {
                    StatusText = "Luồng sự kiện đã kết thúc trước khi phiên chạy hoàn tất.";
                }

                IsBusy = false;
                RefreshCommands();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            RunOnUi(() =>
            {
                StatusText = "Đã dừng theo dõi sự kiện.";
                IsBusy = false;
                RefreshCommands();
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                StatusText = $"Lỗi: {ex.Message}";
                IsBusy = false;
                RefreshCommands();
            });
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_subscriptionCts, cts))
                {
                    _subscriptionCts = null;
                }
            }

            cts.Dispose();
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        string? runId;
        lock (_gate)
        {
            runId = _currentRunId;
        }

        if (runId is null)
        {
            return;
        }

        lock (_gate)
        {
            _subscriptionCts?.Cancel();
            _isCancelRequested = true;
        }
        RunOnUi(() =>
        {
            StatusText = "Đang hủy phiên chạy...";
            RefreshCommands();
        });

        try
        {
            await _client.CancelAsync(runId, cancellationToken).ConfigureAwait(false);
            RunOnUi(() =>
            {
                if (string.Equals(_currentRunId, runId, StringComparison.Ordinal))
                {
                    _currentRunId = null;
                    _waitKind = null;
                    PendingApprovalId = null;
                    PendingApprovalSummary = null;
                    CurrentRun = null;
                }

                StatusText = "Đã gửi yêu cầu hủy.";
                IsBusy = false;
                RefreshCommands();
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                StatusText = $"Lỗi hủy: {ex.Message}";
                IsBusy = false;
                RefreshCommands();
            });
        }
    }

    public async Task ContinueAsync(CancellationToken cancellationToken = default)
    {
        string? runId;
        lock (_gate)
        {
            runId = _currentRunId;
        }

        if (runId is null)
        {
            return;
        }

        try
        {
            await _client.ContinueAsync(runId, cancellationToken).ConfigureAwait(false);
            RunOnUi(() =>
            {
                _waitKind = null;
                StatusText = "Đã gửi yêu cầu tiếp tục.";
                RefreshCommands();
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                StatusText = $"Lỗi tiếp tục: {ex.Message}";
                RefreshCommands();
            });
        }
    }
    public async Task TrustAsync(bool approve, CancellationToken cancellationToken = default)
    {
        var run = CurrentRun;
        if (run is null
            || string.IsNullOrWhiteSpace(run.TrustRequestId)
            || string.IsNullOrWhiteSpace(run.DisplayIdentityHash))
        {
            throw new InvalidOperationException("No workspace trust request is pending.");
        }

        RunOnUi(() => StatusText = approve
            ? "Đang xác nhận workspace..."
            : "Đang từ chối workspace...");
        await _client.TrustAsync(
            run.RunId,
            new TrustAgentRunRequest(
                run.TrustRequestId,
                run.DisplayIdentityHash,
                approve ? "approve" : "deny"),
            cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApproveAsync(
        string scope = "once",
        CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.RunId;
        var approvalId = PendingApprovalId;
        if (runId is null || approvalId is null)
        {
            throw new InvalidOperationException("No Agent approval is pending.");
        }

        RunOnUi(() => StatusText = "Đang gửi phê duyệt...");
        await _client.ApproveAsync(runId, approvalId, scope, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DenyAsync(CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.RunId;
        var approvalId = PendingApprovalId;
        if (runId is null || approvalId is null)
        {
            throw new InvalidOperationException("No Agent approval is pending.");
        }

        RunOnUi(() => StatusText = "Đang từ chối phê duyệt...");
        await _client.DenyAsync(runId, approvalId, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var runId = CurrentRun?.RunId;
        if (runId is null)
        {
            return;
        }

        var run = await _client.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        RunOnUi(() =>
        {
            if (!string.Equals(_currentRunId, run.RunId, StringComparison.Ordinal))
            {
                return;
            }

            CurrentRun = run;
            _waitKind = run.HistoryWaitKind;
            if (!string.Equals(run.Status, "waiting_approval", StringComparison.Ordinal))
            {
                PendingApprovalId = null;
                PendingApprovalSummary = null;
            }

            RefreshCommands();
        });
    }
    public Task<long> SetSessionPresetAsync(
        string sessionId,
        string preset,
        long expectedVersion,
        bool interactive,
        CancellationToken cancellationToken = default) =>
        _client.SetSessionPresetAsync(
            sessionId,
            preset,
            expectedVersion,
            interactive,
            cancellationToken);



    private async Task OnEventAsync(AgentEventModel model)
    {
        if (model.IsTransient)
        {
            // Raw transient output is intentionally not part of the durable event
            // collection. AgentActivityModel travels through the separate callback.
            return;
        }

        RunOnUi(() =>
        {
            if (Events.Count > 200)
            {
                Events.RemoveAt(0);
            }

            Events.Add(model);
            if (!string.IsNullOrEmpty(model.SafeSummary))
            {
                StatusText = model.SafeSummary;
            }

            if (!model.IsTransient && model.EventType == "continue.requested")
            {
                _waitKind = "waiting_user";
            }

            if (!model.IsTransient
                && model.EventType == "approval.requested"
                && !string.IsNullOrWhiteSpace(model.ApprovalId))
            {
                PendingApprovalId = model.ApprovalId;
                PendingApprovalSummary = model.SafeSummary;
            }
            else if (!model.IsTransient
                && model.EventType == "run.state_changed"
                && string.Equals(model.SafeSummary, "approval_resolved", StringComparison.Ordinal))
            {
                PendingApprovalId = null;
                PendingApprovalSummary = null;
            }

            if (!model.IsTransient)
            {
                // AgentAuditLog is UI-bound through the composition. Keep both the
                // collection mutation and sink write on the same dispatcher as the
                // event list to avoid cross-thread ObservableCollection access.
                _auditSink?.Invoke(model);
                WriteSink(model);
            }

            RefreshCommands();
        });

        await Task.CompletedTask;
    }

    private Task OnActivityAsync(AgentActivityModel model)
    {
        lock (_gate)
        {
            if (!_isBusy || _isCancelRequested)
            {
                return Task.CompletedTask;
            }
        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!_isBusy || _isCancelRequested)
                    {
                        return;
                    }
                }

                ActivityReceived?.Invoke(this, model);
            }
            catch
            {
                // A visual activity subscriber is observational and must not fail the
                // Agent SSE subscription or its durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private Task OnSummaryAsync(AgentRunSummaryModel model)
    {
        lock (_gate)
        {
            if (!_isBusy || _isCancelRequested)
            {
                return Task.CompletedTask;
            }
        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!_isBusy || _isCancelRequested)
                    {
                        return;
                    }
                }

                SummaryReceived?.Invoke(this, model);
            }
            catch
            {
                // A visual summary subscriber is observational and must not fail the
                // Agent SSE subscription or durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private Task OnAssistantDeltaAsync(AgentAssistantDeltaModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        string? runId;
        lock (_gate)
        {
            if (!_isBusy || _isCancelRequested || _currentRunId is null)
            {
                return Task.CompletedTask;
            }

            runId = _currentRunId;
        }

        RunOnUi(() =>
        {
            try
            {
                lock (_gate)
                {
                    if (!_isBusy
                        || _isCancelRequested
                        || !string.Equals(_currentRunId, runId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    var key = $"{runId}:{model.ModelStepId}:{model.TransientSequence}";
                    if (!_assistantDeltaKeys.Add(key))
                    {
                        return;
                    }
                }

                AssistantDeltaReceived?.Invoke(this, model);
            }
            catch
            {
                // A visual delta subscriber is observational and must not fail the
                // Agent SSE subscription or its durable run state.
            }
        });
        return Task.CompletedTask;
    }

    private void WriteSink(AgentEventModel model)
    {
        if (_sink is null)
        {
            return;
        }

        var code = model.EventType switch
        {
            "approval.requested" => "agent.approval.required",
            "run.failed" or "run.state_changed" when model.Status == "cancelled" => "agent.cancelled",
            _ => "agent.activity",
        };

        try
        {
            _sink.Write(code, new Dictionary<string, object?>
            {
                ["event_type"] = model.EventType,
                ["safe_summary"] = model.SafeSummary,
                ["sequence"] = model.Sequence,
            });
        }
        catch
        {
            // sink allowlist/validation failures must not break the run feed
        }
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanContinue));
    }

    private void RunOnUi(Action action)
    {
        if (_synchronize is not null)
        {
            _synchronize(action);
        }
        else
        {
            action();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<AgentActivityModel>? ActivityReceived;

    public event EventHandler<AgentRunSummaryModel>? SummaryReceived;

    public event EventHandler<AgentAssistantDeltaModel>? AssistantDeltaReceived;

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static bool IsTerminalStatus(string? status) => status is
        "completed" or "failed" or "cancelled" or "interrupted";

    public void Dispose()
    {
        CancellationTokenSource? subscription;
        lock (_gate)
        {
            subscription = _subscriptionCts;
            _subscriptionCts = null;
            subscription?.Cancel();
        }

        subscription?.Dispose();
        if (_client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
