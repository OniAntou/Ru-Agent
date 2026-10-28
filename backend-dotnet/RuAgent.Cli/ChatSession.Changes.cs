using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private async Task ShowChangesAsync(string? argument, CancellationToken ct)
        {
            if (!await RequireSavedSessionAsync().ConfigureAwait(false)) return;
            var owner = sessionId;
            var run = runSession.ActiveRunId ?? runSession.ParkedRunId ?? runSession.FollowableRunId;
            try
            {
                await new ChatChangesWorkflow(_agentClient).ExecuteAsync(owner, run, argument,
                    tgMode && !cmd.Json ? (list, token) => TgPanelRequests.RequestChoiceAsync(tgWindow!, list, token) : null,
                    tgMode && !cmd.Json ? (title, body, token) => TgPanelRequests.RequestDocumentAsync(tgWindow!, title, body, token) : null,
                    cmd.Json ? _out : humanOut, cmd.Json, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var message = ex is AgentApiException api ? CliErrorText.Format(api) : CliErrorText.ShortMessage(ex.Message);
                await WriteDiagnosticLineAsync(_err, "changes_error: " + ChatDisplayText.Sanitize(message, 256)).ConfigureAwait(false);
            }
        }
    }
}
