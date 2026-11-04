using RuAgent.Protocol.Harness;
using RuAgent.Client;

namespace RuApp.Models;

/// <summary>Bounded display data for one terminal activity; never an execution request.</summary>
public sealed partial class AgentActivityItem
{
    /// <summary>Streamed output a terminal row keeps, so a runaway command cannot grow the row without limit.</summary>
    public const int MaximumTerminalOutputCharacters = 32 * 1024;

    private string _terminalOutputText = string.Empty;
    private string _terminalInputPreview = string.Empty;

    // Only the Host's existing display-safe Detail; never loaded from raw tool arguments.
    public string TerminalInputPreview => _terminalInputPreview;
    public string TerminalInputDisplayText => TerminalInputPreview.Length == 0 ? "Command details unavailable."
        : "$ " + TerminalInputPreview.Replace("\r\n", "\n").Replace("\n", "\n$ ");
    public string TerminalOutputText => _terminalOutputText;
    public bool HasTerminalOutput => _terminalOutputText.Length > 0;
    public bool HasTerminalDetails => Kind == AgentActivityKind.Terminal
        && (TerminalInputPreview.Length > 0 || HasTerminalOutput);
    public bool ShowTerminalOutput => HasTerminalDetails && IsEditPreviewExpanded;
    public bool ShowRowDetailToggle => HasEditPreview || HasTerminalDetails;
    public string DetailToggleLabel => Kind == AgentActivityKind.Terminal ? "Toggle terminal details" : "Toggle edit preview";
    public string TerminalTitle => string.IsNullOrWhiteSpace(ToolDisplay) ? "Terminal" : ToolDisplay;
    public bool TerminalOutputTruncated { get; private set; }
    public bool TerminalHasFailed => IsFailed || ExitCode is { } code && code != 0;

    public string TerminalStatusText => ExitCode is { } code ? $"exit code {code}"
        : IsWaiting ? "Waiting for approval" : IsRunning ? "Running"
        : IsFailed ? (SafeCode is "succeeded" or "" ? "Failed" : SafeCode) : "Completed";

    public string TerminalOutputDisplayText => HasTerminalOutput ? TerminalOutputText
        : IsRunning ? "Waiting for output…" : IsWaiting ? "The command is waiting for approval."
        : "No streamed output captured.";

    /// <summary>The existing Copy action includes both command and captured output.</summary>
    public string TerminalPanelText => HasTerminalDetails
        ? (TerminalInputPreview.Length > 0 ? "$ " + TerminalInputPreview : string.Empty)
            + (HasTerminalOutput ? "\n\n" + TerminalOutputText : string.Empty)
        : string.Empty;

    public void AppendTerminalOutput(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        var combined = _terminalOutputText + chunk;
        if (combined.Length > MaximumTerminalOutputCharacters)
        {
            combined = combined[^MaximumTerminalOutputCharacters..];
            TerminalOutputTruncated = true;
        }
        if (_terminalOutputText == combined) return;
        _terminalOutputText = combined;
        NotifyTerminalOutputChanged();
    }

    public void ResetTerminalOutput()
    {
        if (_terminalOutputText.Length == 0 && !TerminalOutputTruncated) return;
        _terminalOutputText = string.Empty;
        TerminalOutputTruncated = false;
        NotifyTerminalOutputChanged();
    }

    private void CaptureTerminalPreview(AgentActivityModel activity)
    {
        if (activity.Kind == AgentActivityKind.Terminal && !string.IsNullOrWhiteSpace(activity.Detail))
            _terminalInputPreview = activity.Detail;
    }

    private void NotifyTerminalPresentation()
    {
        NotifyTerminalOutputChanged();
        OnPropertyChanged(nameof(TerminalInputPreview));
        OnPropertyChanged(nameof(TerminalInputDisplayText));
        OnPropertyChanged(nameof(TerminalTitle));
        OnPropertyChanged(nameof(TerminalStatusText));
        OnPropertyChanged(nameof(TerminalHasFailed));
        OnPropertyChanged(nameof(DetailToggleLabel));
    }

    private void NotifyTerminalOutputChanged()
    {
        OnPropertyChanged(nameof(TerminalOutputText));
        OnPropertyChanged(nameof(TerminalOutputDisplayText));
        OnPropertyChanged(nameof(TerminalPanelText));
        OnPropertyChanged(nameof(TerminalOutputTruncated));
        OnPropertyChanged(nameof(HasTerminalOutput));
        OnPropertyChanged(nameof(HasTerminalDetails));
        OnPropertyChanged(nameof(ShowTerminalOutput));
        OnPropertyChanged(nameof(ShowRowDetailToggle));
        OnPropertyChanged(nameof(ShowPlainHeader));
    }
}
