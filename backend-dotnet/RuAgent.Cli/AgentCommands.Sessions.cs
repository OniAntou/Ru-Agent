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
    public async Task<int> SessionListAsync(SessionListCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var element = await _sessionClient.ListAsync(cancellationToken).ConfigureAwait(false);
            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonEnvelope("session_list", element)).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync(Tui.TuiPalette.BoldPaint("Active Sessions:")).ConfigureAwait(false);
            if (element.TryGetProperty("sessions", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString();
                    var title = item.GetProperty("title").GetString();
                    var msgCount = item.TryGetProperty("message_count", out var mc) ? mc.GetInt32() : 0;
                    await _out.WriteLineAsync($"  • {Tui.TuiPalette.Accent(id ?? "")} - {title} ({msgCount} messages)").ConfigureAwait(false);
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_list_error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Writes one of Ru's sessions out in another agent's format.
    /// </summary>
    /// <remarks>
    /// The files land where the named agent keeps its sessions unless <c>--out</c> says
    /// otherwise. That default is the point of the command: an export the caller has to
    /// move into place by hand is not really "continue this in Claude Code".
    /// </remarks>
    public async Task<int> SessionExportAsync(SessionExportCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            var interchange = new SessionInterchangeApiClient(_session, _httpClient);
            var result = await interchange.ExportAsync(cmd.SessionId, cmd.Harness, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<RuAgent.SessionInterchange.SessionFileTarget> targets;
            if (cmd.OutputDirectory is { Length: > 0 } directory)
            {
                targets =
                [
                    .. result.Files.Select(file => new RuAgent.SessionInterchange.SessionFileTarget(
                        Path.Combine(directory, file.Name),
                        Append: false)),
                ];
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(home))
                {
                    await _err.WriteLineAsync("session_export_no_profile: cannot locate the user profile directory.")
                        .ConfigureAwait(false);
                    return 1;
                }

                targets = RuAgent.SessionInterchange.SessionFileLayout.TargetsFor(
                    result.Harness,
                    [.. result.Files.Select(file => file.Name)],
                    result.WorkingDirectory,
                    result.CreatedAt,
                    home);
            }

            var written = new List<string>(targets.Count);
            for (var index = 0; index < targets.Count && index < result.Files.Count; index++)
            {
                var target = targets[index];
                var parent = Path.GetDirectoryName(target.Path);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                if (target.Append && File.Exists(target.Path))
                {
                    await File.AppendAllTextAsync(target.Path, result.Files[index].Content, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await File.WriteAllTextAsync(target.Path, result.Files[index].Content, cancellationToken)
                        .ConfigureAwait(false);
                }

                written.Add(target.Path);
            }

            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonEnvelope("session_export", new
                {
                    status = "ok",
                    harness = result.Harness,
                    files = written,
                })).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync($"Exported {cmd.SessionId} as {result.DisplayName}:").ConfigureAwait(false);
            foreach (var path in written)
            {
                await _out.WriteLineAsync($"  • {Tui.TuiPalette.Accent(path)}").ConfigureAwait(false);
            }

            await _out.WriteLineAsync("  Restart that agent to pick the session up.").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_export_error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    /// Turns another agent's session file into a Ru session.
    /// </summary>
    /// <remarks>
    /// The format is read from the file name when <c>--harness</c> is absent — a rollout
    /// names its thread, a Claude Code transcript is named by its session id — and the
    /// command asks instead of guessing when neither fits.
    /// </remarks>
    public async Task<int> SessionImportAsync(SessionImportCommand cmd, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(cmd.FilePath))
            {
                await _err.WriteLineAsync($"session_import_error: no such file '{cmd.FilePath}'.").ConfigureAwait(false);
                return 1;
            }

            var harness = cmd.Harness ?? RuAgent.SessionInterchange.SessionFileLayout.HarnessFromFileName(cmd.FilePath);
            if (harness is null)
            {
                await _err.WriteLineAsync(
                    "session_import_error: cannot tell which agent wrote this file. Pass --harness.")
                    .ConfigureAwait(false);
                return 1;
            }

            string content;
            if (string.Equals(harness, RuAgent.SessionInterchange.SessionFileLayout.OpenCodeId, StringComparison.OrdinalIgnoreCase))
            {
                // opencode keeps every session in one database, so the argument is the store
                // and the session has to be named — the most recent one when nobody named it,
                // and it says which one it took rather than picking in silence.
                var openCodeSessionId = cmd.SessionId;
                if (string.IsNullOrWhiteSpace(openCodeSessionId))
                {
                    var sessions = RuAgent.SessionInterchange.OpenCodeDatabase.ListSessions(cmd.FilePath);
                    if (sessions.Count == 0)
                    {
                        await _err.WriteLineAsync($"session_import_error: '{cmd.FilePath}' holds no opencode sessions.")
                            .ConfigureAwait(false);
                        return 1;
                    }

                    openCodeSessionId = sessions[0].Id;
                    await (cmd.Json ? _err : _out).WriteLineAsync(
                        $"  taking opencode session {Tui.TuiPalette.Accent(openCodeSessionId)} ({sessions[0].Title}).")
                        .ConfigureAwait(false);
                }

                content = RuAgent.SessionInterchange.OpenCodeDatabase.ReadSessionPayload(cmd.FilePath, openCodeSessionId);
            }
            else
            {
                content = await File.ReadAllTextAsync(cmd.FilePath, cancellationToken).ConfigureAwait(false);
            }
            var interchange = new SessionInterchangeApiClient(_session, _httpClient);
            var result = await interchange.ImportAsync(harness, content, cancellationToken).ConfigureAwait(false);

            if (cmd.Json)
            {
                await _out.WriteLineAsync(JsonEnvelope("session_import", new
                {
                    status = "ok",
                    harness,
                    session_id = result.SessionId,
                    messages = result.Messages,
                    dropped_entries = result.DroppedEntries,
                })).ConfigureAwait(false);
                return 0;
            }

            await _out.WriteLineAsync(
                $"Imported {result.Messages} messages from {harness} as session {Tui.TuiPalette.Accent(result.SessionId)}.")
                .ConfigureAwait(false);

            if (result.DroppedEntries > 0)
            {
                await _out.WriteLineAsync(
                    $"  {result.DroppedEntries} entries stayed behind: Ru's session log holds turns, and tool traffic belongs to runs.")
                    .ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"session_import_error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

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
