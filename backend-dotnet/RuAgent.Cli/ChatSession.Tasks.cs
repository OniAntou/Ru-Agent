using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private async Task ShowTasksAsync(string? argument, CancellationToken ct)
        {
            if (!await RequireSavedSessionAsync().ConfigureAwait(false)) return;
            // Capture identity before the first await; task control does not follow a later session/run selection.
            var owner = sessionId;
            var run = runSession.ActiveRunId ?? runSession.ParkedRunId ?? runSession.FollowableRunId;
            try
            {
                await new ChatTaskWorkflow(_agentClient).ExecuteAsync(owner, run, argument,
                    tgMode && !cmd.Json ? (list, token) => TgPanelRequests.RequestChoiceAsync(tgWindow!, list, token) : null,
                    cmd.Json ? _out : humanOut, cmd.Json, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var message = ex is RuAgent.Client.AgentApiException api ? CliErrorText.Format(api) : CliErrorText.ShortMessage(ex.Message);
                await WriteDiagnosticLineAsync(_err, "tasks_error: " + ChatDisplayText.Sanitize(message, 256)).ConfigureAwait(false);
            }
        }
    }
}
