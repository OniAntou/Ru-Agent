using RuApp.Services;

namespace RuApp.Views;

/// <summary>
/// The composer's <c>/advisor</c> command (ADR 0047). The bare command opens the model picker
/// to choose the advisor, <c>/advisor off</c> turns it off, and neither reaches the agent.
/// </summary>
public sealed partial class ChatView
{
    private const string AdvisorUsage = "Use /advisor to choose the advisor model, or /advisor off to turn it off.";

    /// <summary>Handles a sent <c>/advisor</c> line; false when the text is not that command.</summary>
    private bool TryHandleAdvisorCommand(string text, int attachmentCount)
    {
        if (attachmentCount != 0 || ComposerSlashCommands.ParseAdvisor(text) is not { } command) return false;
        Composer.ClearText();
        switch (command)
        {
            case ComposerAdvisorCommand.Pick:
                OpenAdvisorPicker();
                break;
            case ComposerAdvisorCommand.Off:
                _ = ApplyAdvisorAsync(ModelSelection.ClearAdvisorAsync(AdvisorSessionId()));
                break;
            default:
                ShowAgentInlineStatus(AdvisorUsage);
                break;
        }

        return true;
    }

    /// <summary>Opens the composer's model picker to choose the advisor rather than the main model.</summary>
    public void OpenAdvisorPicker()
    {
        if (ChatModelButton is null || ChatModelFlyout is null) return;
        ModelSelection.BeginAdvisorPick();
        ShowAgentInlineStatus(ModelSelection.AdvisorLabel is { } current
            ? $"Choose the advisor (now {current}), or send /advisor off."
            : "Choose the advisor model the agent may consult in this chat.");
        ChatModelFlyout.ShowAt(ChatModelButton);
    }

    /// <summary>The open chat's session, or null before its first message creates one.</summary>
    private string? AdvisorSessionId() =>
        AgentSessionSelection.IsValid(_activeSessionId) ? _activeSessionId : null;

    /// <summary>
    /// Shows the advisor of the session being opened. Best effort: a read failure shows none.
    /// </summary>
    private async Task AdoptSessionAdvisorAsync(string sessionId, CancellationToken cancellationToken)
    {
        var advisor = _apiService is null ? null : await _apiService.GetSessionAdvisorAsync(sessionId, cancellationToken);
        if (!string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal)) return;
        ModelSelection.ShowSessionAdvisor(advisor?.Backend, advisor?.Model);
    }

    /// <summary>
    /// Saves an advisor chosen before the chat's session existed to the session its first
    /// message just created. A failure is shown: that run starts without the advisor.
    /// </summary>
    private async Task ApplyPendingAdvisorAsync(string sessionId)
    {
        if (!await ModelSelection.ApplyPendingAdvisorAsync(sessionId))
        {
            ShowAgentInlineStatus("The advisor could not be saved for this chat, so this run has none. Choose it again with /advisor.");
        }
    }

    private async Task ApplyAdvisorAsync(Task<string> save)
    {
        try
        {
            ShowAgentInlineStatus(await save);
        }
        catch (Exception error)
        {
            GuiDiagnosticWriter.AppendTraceLine($"[ChatView] Advisor save failed: {error.GetType().Name}");
        }
    }
}
