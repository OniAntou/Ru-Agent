using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    public async Task<int> RunAsync(RunCommand cmd, CancellationToken cancellationToken)
    {
        RunStartOutcome outcome;
        try
        {
            outcome = await _runStarter.StartAsync(
                new RunStartRequest(cmd.WorkspacePath, cmd.Prompt, cmd.Preset, cmd.SessionId, cmd.TrustWorkspace, cmd.ConfirmFullAccess, cmd.Json),
                async sessionId =>
                {
                    // --json owns stdout: human-facing ids go to stderr so the NDJSON
                    // trace stays machine-parseable line by line.
                    await (cmd.Json ? _err : _out).WriteLineAsync($"session_id: {sessionId}").ConfigureAwait(false);
                },
                cancellationToken,
                async run =>
                {
                    await (cmd.Json ? _err : _out).WriteLineAsync($"session_id: {run.SessionId}").ConfigureAwait(false);
                    await (cmd.Json ? _err : _out).WriteLineAsync($"run_id: {run.RunId}").ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (RunStartException)
        {
            // The core already wrote the exact operator line to the exact stream.
            return 1;
        }

        try
        {
            await StreamEventsAsync(outcome.RunId, cmd.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                // A transport timeout is not the operator's cancel, and the host may
                // still own the run; do not cancel it, point at attach instead.
                await _err.WriteLineAsync("stream_timeout: the event stream did not complete in time; rerun 'ru agent attach <run-id>' to follow.").ConfigureAwait(false);
                return 1;
            }

            try
            {
                await _agentClient.CancelAsync(outcome.RunId, CancellationToken.None).ConfigureAwait(false);
                await (cmd.Json ? _err : _out).WriteLineAsync("cancelled").ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            return 130;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }

        return 0;
    }

    public async Task<int> AttachAsync(AttachCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _agentClient.GetRunAsync(cmd.RunId, cancellationToken).ConfigureAwait(false);
            if (!cmd.Json)
            {
                await _out.WriteLineAsync($"run_id: {run.RunId} status: {run.Status}").ConfigureAwait(false);
            }
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }

        try
        {
            await StreamEventsAsync(cmd.RunId, cmd.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await _err.WriteLineAsync("stream_timeout: the event stream did not complete in time; rerun 'ru agent attach <run-id>' to follow.").ConfigureAwait(false);
                return 1;
            }

            try { await _agentClient.CancelAsync(cmd.RunId, CancellationToken.None).ConfigureAwait(false); } catch { }
            return 130;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
        return 0;
    }

    public async Task<int> OutputAsync(OutputCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var content = await _agentClient.GetPayloadAsync(cmd.RunId, cmd.PayloadId, cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync(content).ConfigureAwait(false);
            return 0;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync($"{CliErrorText.Format(ex)}").ConfigureAwait(false);
            return 1;
        }
    }

    private async Task StreamEventsAsync(string runId, bool json, CancellationToken ct)
    {
        await StreamRunEventsResumedAsync(runId, async (frame, frameCt) =>
        {
            frameCt.ThrowIfCancellationRequested();
            if (json)
            {
                NdjsonEventRenderer.RenderFrame(frame, _out);
            }
            else
            {
                SafeEventRenderer.RenderFrame(frame, _out);
            }
            await _out.FlushAsync().ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
