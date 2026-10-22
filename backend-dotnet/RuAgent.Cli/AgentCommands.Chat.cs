using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Cli.Attachments;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private static string ReadReasoningEffort(System.Text.Json.JsonElement settings)
    {
        if (settings.TryGetProperty("reasoning_effort", out var direct) && !string.IsNullOrWhiteSpace(direct.GetString()))
        {
            return direct.GetString()!;
        }
        if (settings.TryGetProperty("thinking_effort", out var thinking) && !string.IsNullOrWhiteSpace(thinking.GetString()))
        {
            return thinking.GetString()!;
        }
        if (settings.TryGetProperty("reasoning", out var reasoning)
            && reasoning.ValueKind == System.Text.Json.JsonValueKind.Object
            && reasoning.TryGetProperty("effort", out var effort)
            && !string.IsNullOrWhiteSpace(effort.GetString()))
        {
            return effort.GetString()!;
        }
        return string.Empty;
    }

    public async Task<int> ChatAsync(ChatCommand cmd, CancellationToken cancellationToken)
    {
        // Validate at the entry and carry the canonical wire value forward.
        // Snapshot factories, header meta and handover presets all read
        // cmd.Preset downstream, so a loosely-accepted "Standard" or
        // " standard" must not survive as the raw string and push the real
        // failure onto the server's response binding.
        if (!RuAgent.Protocol.Harness.AgentPolicyProfileCatalog.TryNormalizePresetInput(cmd.Preset, out var normalizedPreset))
        {
            await _err.WriteLineAsync("invalid_preset: preset must be plan, standard, or full_access").ConfigureAwait(false);
            return 1;
        }

        cmd = cmd with { Preset = normalizedPreset };

        if (cmd.Preset == "full_access" && !cmd.ConfirmFullAccess)
        {
            await _err.WriteLineAsync("full_access_confirmation_required: --confirm-full-access is required.").ConfigureAwait(false);
            return 1;
        }

        await using var bootstrap = await PrepareChatAsync(cmd, cancellationToken).ConfigureAwait(false);
        if (bootstrap is null)
        {
            return 1;
        }


        return await new ChatSession(this, cmd, bootstrap, cancellationToken).RunAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Routes one submitted line to the slash-command handler or the prompt runner. The
    /// sequential and Terminal.Gui loops both call this so command behaviour
    /// (attachments, /model, trust prompts) cannot drift between engines.
    /// </summary>
    public sealed record ChatLineDispatcher
    {
        public ChatLineDispatcher(
            Func<string, Task<bool>> handleSlash,
            Func<string, CancellationToken, Task> handlePrompt)
        {
            HandleSlash = handleSlash;
            HandlePrompt = handlePrompt;
        }

        public Func<string, Task<bool>> HandleSlash { get; init; }

        public Func<string, CancellationToken, Task> HandlePrompt { get; init; }

        public async Task<bool> DispatchAsync(string line, CancellationToken cancellationToken)
        {
            if (line.TrimStart().StartsWith('/'))
            {
                return await HandleSlash(line).ConfigureAwait(false);
            }

            await HandlePrompt(line, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Picks the writer a diagnostic line lands on. While Terminal.Gui owns the frame the
    /// bridge writer keeps the line in the transcript; once the tiny-terminal handover has
    /// disposed the app (or no bridge exists) the plain writer must take over, or every
    /// line after the handover disappears into a stopped bridge.
    /// </summary>
    internal static Tui.TuiSessionRenderer CreateSessionRenderer(TextWriter writer)
    {
        var renderer = new Tui.TuiSessionRenderer(writer);
        renderer.Transcript.FullScreenCards = writer is TgTranscriptWriter;
        return renderer;
    }
    public static TextWriter ResolveDiagnosticWriter(bool tgMode, TextWriter? tgWriter, TextWriter fallback) =>
        tgMode && tgWriter is not null ? tgWriter : fallback;

    /// <summary>
    /// A busy session is operator-recoverable: name the run that holds it and how to
    /// release it instead of printing the raw error envelope. The wire code stays
    /// `workspace_busy` for compatibility; the conflict itself is session-scoped.
    /// Every line goes through <paramref name="writeLineAsync"/> so it lands on the
    /// resolved writer.
    /// </summary>
    private async Task WriteBusyConflictAsync(
        AgentApiException error,
        Func<TextWriter, string, Task> writeLineAsync,
        Action<string, string>? markBusy = null)
    {
        ArgumentNullException.ThrowIfNull(writeLineAsync);
        var blockingRunId = error.BlockingRunId;
        if (string.IsNullOrWhiteSpace(blockingRunId))
        {
            await writeLineAsync(_err, Tui.TuiPalette.Err("workspace_busy: another Agent run still owns this session.")).ConfigureAwait(false);
            await writeLineAsync(_err, Tui.TuiPalette.DimPaint("  release the blocking run with 'ru agent cancel <run-id>'.")).ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }

        var status = string.IsNullOrWhiteSpace(error.BlockingRunStatus) ? "active" : error.BlockingRunStatus;
        markBusy?.Invoke(blockingRunId, status);
        await writeLineAsync(_err, Tui.TuiPalette.Err($"workspace_busy: run {blockingRunId} ({status}) still owns this session.")).ConfigureAwait(false);
        var hint = IsParkedRunStatus(status)
            ? $"  answer it with 'ru agent attach {blockingRunId}', continue it with 'ru agent continue {blockingRunId}', or release it with 'ru agent cancel {blockingRunId}'."
            : $"  answer it with 'ru agent attach {blockingRunId}', or release it with 'ru agent cancel {blockingRunId}'.";
        await writeLineAsync(_err, Tui.TuiPalette.DimPaint(hint)).ConfigureAwait(false);
        await _err.FlushAsync().ConfigureAwait(false);
    }

}
