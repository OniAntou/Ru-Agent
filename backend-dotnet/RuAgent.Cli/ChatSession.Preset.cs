using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private async Task ChangePresetAsync(string? argument)
        {
            var selection = await ChatPresetSelection.SelectAsync(cmd, argument,
                runSession.IsTurnActive
                    || runSession.ParkedRunId is not null || runSession.FollowableRunId is not null,
                tgMode && !cmd.Json ? _slashCommands.Picker : null, cancellationToken).ConfigureAwait(false);
            if (selection.Error is not null)
            {
                await WriteDiagnosticLineAsync(_err, selection.Error).ConfigureAwait(false);
                return;
            }
            if (selection.ManageRules)
            {
                try
                {
                    await new ChatApprovalRulesWorkflow(_agentClient, _sessionClient).ExecuteAsync(
                        canonicalWorkspace, tgMode && !cmd.Json ? _slashCommands.Picker : null,
                        tgMode && !cmd.Json ? (title, rows, ct) => TerminalGui.TgPanelRequests.RequestDocumentAsync(tgWindow!, title, rows, ct) : null,
                        cmd.Json ? _out : humanOut, cmd.Json, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var code = ex is RuAgent.Client.AgentApiException api ? ChatDisplayText.Sanitize(api.ErrorCode, 96) : "rules_unavailable";
                    await WriteDiagnosticLineAsync(_err, $"{code}: could not confirm saved rules; refresh before retrying.").ConfigureAwait(false);
                }
                return;
            }
            if (selection.Command is null) return;

            cmd = selection.Command;
            if (tgMode) RunOnTgUi(() => tgWindow!.Composer.SetMeta(cmd.Preset, activeModel, backendName));
            await WriteDiagnosticLineAsync(humanOut,
                TuiPalette.Ok($"preset: {cmd.Preset} · applies to the next new turn")).ConfigureAwait(false);
        }
    }
}
