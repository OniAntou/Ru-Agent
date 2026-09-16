using System.ComponentModel;

namespace RuApp.Services;

/// <summary>
/// The busy flag and status line for an operation a controller runs on the user's
/// behalf: what is happening, how it ended, and whether another operation may start.
///
/// Two controllers hand-rolled this pair, and one of them repeated the same
/// begin/run/assign/finally shape in three methods. The shape lives here so the
/// sequencing — busy on, outcome assigned once, busy off even when the call throws —
/// is stated once instead of per method.
/// </summary>
public sealed class OperationStatus : INotifyPropertyChanged
{
    private readonly object _gate = new();
    private readonly Action<Action> _runOnUi;
    private string _statusText;
    private bool _isBusy;

    /// <param name="runOnUi">
    /// Marshals an assignment onto the UI thread. Assignments are raised from
    /// continuations, so a controller on a background thread would otherwise touch
    /// bound state off-thread.
    /// </param>
    /// <param name="initialStatus">The idle message before anything has run.</param>
    public OperationStatus(Action<Action> runOnUi, string initialStatus)
    {
        _runOnUi = runOnUi ?? throw new ArgumentNullException(nameof(runOnUi));
        _statusText = initialStatus ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get { lock (_gate) { return _isBusy; } }
        private set
        {
            lock (_gate) { _isBusy = value; }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsBusy)));
        }
    }

    public string StatusText
    {
        get { lock (_gate) { return _statusText; } }
        private set
        {
            lock (_gate) { _statusText = value; }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> with the status line set, marks the controller
    /// busy for its duration, reports one outcome, and always returns to idle. The
    /// operation's exception propagates; only the idle transition is guaranteed.
    /// </summary>
    /// <param name="announce">The status shown while the operation runs.</param>
    /// <param name="operation">The work. Its result is handed to <paramref name="succeeded"/>.</param>
    /// <param name="succeeded">
    /// Builds the status for a successful outcome. Runs on the UI thread, so it may
    /// touch bound collections directly.
    /// </param>
    /// <param name="failed">Builds the status for a failure.</param>
    public async Task RunAsync<T>(
        string announce,
        Func<Task<T>> operation,
        Func<T, string> succeeded,
        Func<Exception, string> failed)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(succeeded);
        ArgumentNullException.ThrowIfNull(failed);

        _runOnUi(() =>
        {
            IsBusy = true;
            StatusText = announce;
        });

        try
        {
            T result = await operation().ConfigureAwait(false);
            _runOnUi(() => StatusText = succeeded(result));
        }
        catch (Exception ex)
        {
            _runOnUi(() => StatusText = failed(ex));
            throw;
        }
        finally
        {
            _runOnUi(() => IsBusy = false);
        }
    }

    /// <summary>
    /// Sets the status without running an operation. For a controller that reports
    /// state it learns from elsewhere, such as a streamed summary.
    /// </summary>
    public void Report(string statusText) => SetStatus(statusText);

    /// <summary>Assigns the status line. Marshals onto the UI thread.</summary>
    public void SetStatus(string? statusText) =>
        _runOnUi(() => StatusText = statusText ?? string.Empty);

    /// <summary>Assigns the busy flag. Marshals onto the UI thread.</summary>
    public void SetBusy(bool isBusy) => _runOnUi(() => IsBusy = isBusy);
}
