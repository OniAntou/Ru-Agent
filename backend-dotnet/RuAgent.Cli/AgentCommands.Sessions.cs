using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Client;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    public async Task<int> SessionRewindAsync(SessionRewindCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var element = await _sessionClient.RewindAsync(cmd.SessionId, cmd.ToMessageIndex, cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonEnvelope("session_rewind", element)).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Successfully rewound session {cmd.SessionId} to message {cmd.ToMessageIndex}")).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_rewind_error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Deletes one session after an explicit confirmation. Without <c>--yes</c> the
    /// terminal operator answers a y/N prompt defaulting to No; a partial cleanup from
    /// the backend surfaces its bounded message instead of reading as success.
    /// </summary>
    public async Task<int> SessionDeleteAsync(SessionDeleteCommand cmd, CancellationToken cancellationToken)
    {
        if (!cmd.Yes)
        {
            await _out.WriteAsync($"Delete session {cmd.SessionId}? This cannot be undone. Confirm (y/N): ").ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
            var answer = await _in.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var normalized = answer?.Trim().ToLowerInvariant();
            if (normalized is not ("y" or "yes"))
            {
                await _err.WriteLineAsync("cancelled: session not deleted.").ConfigureAwait(false);
                return 1;
            }
        }

        try
        {
            await _sessionClient.DeleteAsync(cmd.SessionId, cancellationToken).ConfigureAwait(false);
            await _out.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Deleted session {cmd.SessionId}")).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentApiException ex)
        {
            await _err.WriteLineAsync(CliErrorText.Format(ex)).ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_delete_error: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// The one --json output shape: every command emits {"v":1,"type":...} so a
    /// consumer can parse any stdout line without knowing which command wrote it.
    /// </summary>
    private static string JsonEnvelope(string type, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload) as JsonObject ?? new JsonObject();
        node["v"] = 1;
        node["type"] = type;
        return node.ToJsonString();
    }
}
