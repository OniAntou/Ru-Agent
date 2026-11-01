using RuAgent.Cli.TerminalGui;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        private ChatInputWorkflow? inputWorkflow;

        private async Task AnswerInputInTgAsync(HarnessSafeEventEnvelope envelope, string expectedRunId, CancellationToken ct)
        {
            string? inputId = null;
            try
            {
                if (!HarnessSafeEventEnvelope.IsValidProtocolVersion(envelope.ProtocolVersion)
                    || envelope.RunId != expectedRunId || envelope.SessionId != sessionId)
                    throw new InvalidOperationException("input_session_mismatch: question does not belong to the active run/session");
                var request = await ReadInputRequestAsync(expectedRunId, envelope.PayloadRef, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("input_request_unavailable: unable to read the pending question");
                inputId = request.InputId;
                await _out.FlushAsync().ConfigureAwait(false);
                var outcome = await (inputWorkflow ??= new ChatInputWorkflow(_agentClient)).AnswerAsync(
                    new ChatInputQuestion(expectedRunId, sessionId, request.InputId, request.Question, request.Options),
                    (question, token) => TgPanelRequests.RequestQuestionAsync(tgWindow!, question, token), ct).ConfigureAwait(false);
                if (outcome == ChatInputOutcome.Deferred)
                {
                    sessionRenderer.Note($"question left pending · use /follow {expectedRunId} to answer", Tui.TuiToken.Warn);
                    chatEngine?.PauseQueuedSubmissions();
                    throw new RunWaitingInputException(expectedRunId, request.InputId);
                }
                if (outcome == ChatInputOutcome.Answered)
                    sessionRenderer.Note("answer sent · continuing the same run", Tui.TuiToken.Ok);
                chatEngine?.ResumeQueuedSubmissions();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (RunParkedException) { throw; }
            catch (Exception ex)
            {
                await WriteDiagnosticLineAsync(_err, $"input_answer_error: {CliErrorText.ShortMessage(ex.Message)} · use /follow {expectedRunId} to check the run").ConfigureAwait(false);
                chatEngine?.PauseQueuedSubmissions();
                throw new RunWaitingInputException(expectedRunId, inputId ?? "unavailable");
            }
        }
    }
}
