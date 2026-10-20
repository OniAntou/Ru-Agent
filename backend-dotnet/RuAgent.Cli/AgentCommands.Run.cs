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
        IReadOnlyList<string>? imagePayloads = null;
        if (cmd.Images is { Count: > 0 })
        {
            var payloads = new List<string>(cmd.Images.Count);
            foreach (var image in cmd.Images)
            {
                var (attachment, error) = await Images.CliImageFiles
                    .LoadAsync(image, cmd.WorkspacePath, cancellationToken)
                    .ConfigureAwait(false);
                if (attachment is null)
                {
                    await _err.WriteLineAsync(error).ConfigureAwait(false);
                    return 1;
                }

                payloads.Add(attachment.ToDataUrl());
            }

            imagePayloads = payloads;
        }

        RunStartOutcome outcome;
        try
        {
            outcome = await _runStarter.StartAsync(
                new RunStartRequest(cmd.WorkspacePath, cmd.Prompt, cmd.Preset, cmd.SessionId, cmd.TrustWorkspace, cmd.ConfirmFullAccess, cmd.Json, imagePayloads),
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
            if (await StreamEventsAsync(outcome.RunId, cmd.Json, cancellationToken).ConfigureAwait(false))
                return 2;
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
            catch (Exception ex)
            {
                // The operator must know the run may still hold its session: a
                // swallowed failure here would resurface later as workspace_busy
                // with no explanation.
                await _err.WriteLineAsync($"run_cancel_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
                await _err.WriteLineAsync($"run {outcome.RunId} may still be running; release it with 'ru agent cancel {outcome.RunId}'.").ConfigureAwait(false);
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
            if (await StreamEventsAsync(cmd.RunId, cmd.Json, cancellationToken).ConfigureAwait(false))
                return 2;
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await _err.WriteLineAsync("stream_timeout: the event stream did not complete in time; rerun 'ru agent attach <run-id>' to follow.").ConfigureAwait(false);
                return 1;
            }

            try
            {
                await _agentClient.CancelAsync(cmd.RunId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Same contract as 'ru agent run': a cancel that failed is
                // reported instead of leaving the run silently alive.
                await _err.WriteLineAsync($"run_cancel_failed: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
                await _err.WriteLineAsync($"run {cmd.RunId} may still be running; release it with 'ru agent cancel {cmd.RunId}'.").ConfigureAwait(false);
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

    private async Task<bool> StreamEventsAsync(string runId, bool json, CancellationToken ct)
    {
        var parked = false;
        string? parkReason = null;
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
            if (RunParkSignal.TryRead(frame, runId, out var reason))
            {
                var current = await _agentClient.GetRunAsync(runId, frameCt).ConfigureAwait(false);
                // Replay can contain old parks followed by a later Continue. Only the
                // current durable boundary may detach this live subscription.
                if (IsParkedRunStatus(current.Status) && current.LastSequence == frame.DurableSequence)
                {
                    parked = true;
                    parkReason = reason;
                }
            }
        }, ct, stopWhen: _ => parked).ConfigureAwait(false);
        if (parked)
        {
            var noticeWriter = json ? _err : _out;
            var notice = $"run parked · {parkReason ?? "reason pending"} · continue with 'ru agent continue {runId}' · abandon with 'ru agent cancel {runId}'";
            await noticeWriter.WriteLineAsync(notice).ConfigureAwait(false);
            await noticeWriter.FlushAsync().ConfigureAwait(false);
        }
        return parked;
    }
}
