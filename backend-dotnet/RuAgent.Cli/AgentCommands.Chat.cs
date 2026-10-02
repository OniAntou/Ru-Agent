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
    /// <summary>How many of a reopened session's newest messages a chat replay may show.</summary>
    private const int SessionReplayMessageLimit = 20;

    /// <summary>Per-message character cap for a chat replay; longer text is shortened with a marker.</summary>
    private const int SessionReplayMaxChars = 2000;

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
        if (cmd.Preset is not ("plan" or "standard" or "full_access"))
        {
            await _err.WriteLineAsync("invalid_preset: preset must be plan, standard, or full_access").ConfigureAwait(false);
            return 1;
        }

        if (cmd.Preset == "full_access" && !cmd.ConfirmFullAccess)
        {
            await _err.WriteLineAsync("full_access_confirmation_required: --confirm-full-access is required.").ConfigureAwait(false);
            return 1;
        }

        string canonicalWorkspace;
        try
        {
            canonicalWorkspace = Path.GetFullPath(cmd.WorkspacePath);
        }
        catch (Exception ex)
        {
            await _err.WriteLineAsync($"invalid_workspace: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        string sessionId = cmd.SessionId ?? string.Empty;
        if (string.IsNullOrEmpty(sessionId))
        {
            try
            {
                sessionId = await _sessionClient.CreateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _err.WriteLineAsync($"session_create_failed: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }

        // The TUI header reports the provider the run is using. When settings cannot
        // be read the header says so rather than naming a provider the run never
        // picked: a substituted value here is indistinguishable from a real one.
        string? backendName = null;
        string activeModel = string.Empty;
        string reasoningEffort = string.Empty;
        try
        {
            var exePath = Environment.ProcessPath;
            var exeStamp = exePath is not null && File.Exists(exePath)
                ? File.GetLastWriteTimeUtc(exePath).ToString("O")
                : "unknown";
            Tui.TuiDebug.Log("build", $"exe={exePath} stamp={exeStamp}");
        }
        catch (Exception)
        {
            // Build stamp is diagnostics-only.
        }
        string? modelWarning = null;
        try
        {
            var settings = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
            backendName = settings.TryGetProperty("backend", out var b) && !string.IsNullOrWhiteSpace(b.GetString())
                ? b.GetString()
                : null;
            activeModel = backendName switch
            {
                "OpenAI" => settings.TryGetProperty("openai_model", out var oam) ? oam.GetString() ?? "" : "",
                "Anthropic" => settings.TryGetProperty("anthropic_model", out var anm) ? anm.GetString() ?? "" : "",
                _ => settings.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
            };
            reasoningEffort = ReadReasoningEffort(settings);
        }
        catch (Exception)
        {
            // Settings backend unreachable; the header reports it as unknown.
        }

        if (backendName is not null)
        {
            // Best-effort and once per chat: a models.dev id that is not in the catalog
            // would otherwise only surface mid-turn as model_unavailable.
            modelWarning = await ModelPrecheck
                .CheckAsync(_settingsClient, backendName, activeModel, cancellationToken)
                .ConfigureAwait(false);
        }

        backendName ??= "(provider unknown)";

        // --json owns stdout: every human-facing line a command prints goes to
        // stderr instead, so a consumer can parse stdout line by line as NDJSON.
        var humanOut = cmd.Json ? _err : _out;

        var ws = Tui.TuiHeader.ShortenWorkspace(canonicalWorkspace, 48);
        var workspaceLabel = string.IsNullOrWhiteSpace(cmd.Preset) ? ws : $"{ws} · {cmd.Preset}";

        var sessionRenderer = new Tui.TuiSessionRenderer(_out);
        var lineReader = new Tui.InteractiveLineReader
        {
            TopLabel = workspaceLabel,
        };

        // Full-screen AltBuffer gate: the same real signals the sticky footer already trusts
        // (a live console, TERM), plus NO_COLOR, the command flags and the live 80x24 minimum.
        // Evaluated once per session: switching buffers mid-session would strand the scrollback.
        // NO_COLOR is honoured on definition, empty string included, exactly like TuiPalette.
        var hasLiveSize = Tui.TuiViewport.TryReadLiveSize(out var liveColumns, out var liveRows);
        var altBufferActive = Tui.TuiAltBuffer.ShouldUseAltBuffer(
            outputRedirected: Console.IsOutputRedirected || !ReferenceEquals(_out, Console.Out),
            term: Environment.GetEnvironmentVariable("TERM"),
            noColor: Environment.GetEnvironmentVariable("NO_COLOR"),
            columns: hasLiveSize ? liveColumns : 0,
            rows: hasLiveSize ? liveRows : 0,
            classic: cmd.Classic,
            json: cmd.Json);

        // Disposed on every exit path (return, cancellation, exception), so the operator's
        // normal screen is restored even when the session dies mid-turn.
        using var altScreen = new Tui.TuiAltBufferScope(sessionRenderer.Surface, altBufferActive);

        // Full-screen transcript rendering (bordered tool cards, highlighted fences) only in
        // the AltBuffer frame; the sequential fallback keeps its existing rows byte-for-byte.
        sessionRenderer.Transcript.FullScreenCards = altBufferActive;

        if (!cmd.Json && _out == Console.Out && !Console.IsOutputRedirected && !altBufferActive)
        {
            try { Console.Clear(); _out.Write("\u001b[3J\u001b[H"); } catch (IOException) { /* No live console to clear. */ }
        }

        if (altBufferActive)
        {
            // The alt buffer starts from a blank frame; the normal buffer's scrollback stays
            // untouched, because leaving the session restores it.
            sessionRenderer.Surface.Repaint(writer => writer.Write("\u001b[2J\u001b[H"));
        }

        // Sticky footer: while a turn runs the live row and composer stay pinned to the bottom
        // rows and agent output scrolls above them. Redirected output, --json and --classic keep
        // the sequential layout.
        var sticky = new Tui.TuiStickyRegion(sessionRenderer.Surface);
        bool stickyEnabled = !cmd.Json
            && !cmd.Classic
            && Tui.TuiViewport.IsLiveConsole
            && !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);

        // The pinned full-screen header. Only the AltBuffer frame has one; the sequential
        // layout keeps its existing header/landing flow untouched.
        void PaintAltHeader()
        {
            if (!altBufferActive)
            {
                return;
            }

            var width = Tui.TuiViewport.Width();
            var layout = Tui.TuiScreenLayout.Compute(width, Tui.TuiViewport.Height());
            Tui.TuiHeader.RenderPinned(
                sessionRenderer.Surface,
                width,
                layout.HeaderTop + 1,
                canonicalWorkspace,
                backendName,
                activeModel,
                cmd.Preset,
                sessionId,
                layout.HeaderHeight);
        }

        // First 1-based row of the transcript region: below the two pinned header rows in the
        // AltBuffer frame, the whole screen in the sequential layout.
        int TranscriptTopRow(int rows) => altBufferActive
            ? Tui.TuiScreenLayout.Compute(Tui.TuiViewport.Width(), rows).TranscriptTop + 1
            : 1;

        // Type-ahead: keystrokes captured during a turn feed the one-slot queue, and a pending
        // approval takes the keyboard over the same pinned footer.
        var pendingPrompt = new PendingPromptSlot();
        var typeAhead = new Tui.TypeAheadSession(pendingPrompt)
        {
            WorkspaceLabel = workspaceLabel,
        };
        string? queuedLine = null;
        CancellationTokenSource? turnCts = null;
        string? activeRunId = null;
        string? parkedRunId = null;
        var assistantPayloadRefs = new List<string>();
        CancellationTokenSource? typeAheadCts = null;
        Task? typeAheadLoop = null;
        bool typeAheadLive = false;

        // Slash-command surfaces: the pickers run through the same key loop as the composer.
        // --json owns stdout, so every human-facing line a slash command prints goes to
        // stderr and picker-only commands refuse instead of prompting.
        _slashCommands.HumanOut = humanOut;
        _slashCommands.Json = cmd.Json;
        _slashCommands.PanelWidth = Tui.TuiViewport.Width();
        _slashCommands.Picker = (list, ct) => cmd.Json
            ? Task.FromResult<string?>(null)
            : altBufferActive && typeAheadLive
                ? RequestChoicePanelAsync(list, ct)
                : lineReader.ReadChoiceAsync(list, _out, ct);
        _slashCommands.SecretPrompt = (prompt, ct) => cmd.Json
            ? Task.FromResult<string?>(null)
            : altBufferActive && typeAheadLive
                ? RequestSecretPanelAsync(null, ct)
                : lineReader.ReadSecretAsync(_out, prompt, ct);
        _slashCommands.SecretPanelPrompt = (providerDisplay, ct) => cmd.Json
            ? Task.FromResult<string?>(null)
            : altBufferActive && typeAheadLive
                ? RequestSecretPanelAsync(providerDisplay, ct)
                : lineReader.ReadSecretPanelAsync(_out, providerDisplay, Tui.TuiViewport.Width(), ct);

        int turn = 0;
        bool landingScreenVisible = true;
        int starEpoch = 0;

        // The composer reopens its own frame after a settled resize; this rebuilds everything
        // around it (region, header, transcript tail) so the new width applies end to end.
        // Always the full in-place rebuild, even without a sticky region: reprinting the
        // header as appended lines is what used to grow the scrollback on every resize.
        lineReader.OnScreenRefreshedAsync = () =>
        {
            RedrawScreenForResize();
            return Task.CompletedTask;
        };

        // Landing star twinkle: the reader polls this while its buffer is empty; a true
        // answer advances the star epoch so the resize-path rebuild paints a new sky.
        // Idle-only by construction: first prompt, approval UI or typed text keeps false.
        lineReader.ShouldTwinkleStars = () =>
        {
            if (!landingScreenVisible || turn != 0)
            {
                return false;
            }

            if (typeAhead.Approval is not null || !string.IsNullOrEmpty(typeAhead.Buffer))
            {
                return false;
            }

            starEpoch++;
            return true;
        };

        void RenderLandingScreen()
        {
            if (cmd.Json || cmd.Classic || !Tui.TuiViewport.IsLiveConsole)
            {
                return;
            }

            var rows = Tui.TuiViewport.Height();
            var width = Tui.TuiViewport.Width();
            PaintAltHeader();
            var headerRows = altBufferActive
                ? Tui.TuiScreenLayout.Compute(width, rows).HeaderHeight
                : 0;
            var landingLines = Tui.TuiHeader.ComposeLandingScreen(
                width,
                canonicalWorkspace,
                backendName,
                activeModel,
                cmd.Preset,
                rows,
                starEpoch);

            foreach (var line in landingLines)
            {
                _out.WriteLine(line);
            }

            // The AltBuffer frame owns its composer in the pinned footer, so the sequential
            // box pad only exists in the fallback layout.
            if (!typeAheadLive && !altBufferActive)
            {
                var boxHeight = !string.IsNullOrWhiteSpace(lineReader.Hints)
                    ? Tui.ComposerBox.Height + 1
                    : Tui.ComposerBox.Height;
                var boxTop = Math.Max(1, rows - boxHeight + 1);
                var cursorRow = landingLines.Count + 1 + headerRows;
                for (var i = cursorRow; i < boxTop; i++)
                {
                    _out.WriteLine(Tui.TuiHeader.StarRow(width, 2500 + i, starEpoch));
                }
            }

            _out.Flush();
        }

        /// <summary>
        /// Recovery for a settled resize. A terminal that changed size has already reflowed the
        /// rows this UI addressed absolutely, so patching the frame can never be reliable; the
        /// deterministic answer is to reset the scrolling region, clear the screen, and rebuild
        /// from the top. The visible transcript is restored from the bounded line history, so a
        /// resize never blanks what the operator was reading.
        /// </summary>
        void RedrawScreenForResize()
        {
            if (cmd.Json)
            {
                // The NDJSON trace has no screen to clear, and Console.Clear/ANSI
                // bytes would corrupt stdout.
                return;
            }

            sticky.Exit();
            sessionRenderer.Status.InFooter = false;
            if (_out == Console.Out && !Console.IsOutputRedirected)
            {
                try { Console.Clear(); } catch (IOException) { }
            }
            sessionRenderer.Surface.Repaint(writer => writer.Write(FullRebuildClearSequence(altBufferActive)));

            var rows = Tui.TuiViewport.Height();
            var width = Tui.TuiViewport.Width();
            sessionRenderer.Width = width;

            if (landingScreenVisible && turn == 0)
            {
                RenderLandingScreen();
                if (altBufferActive && typeAheadLive)
                {
                    // The landing rebuild left the footer region down; the persistent
                    // idle composer needs it back in the same settled resize.
                    ApplyFooterLayout(repaint: true);
                }

                return;
            }

            PaintAltHeader();

            var footerHeight = Tui.TuiStickyRegion.ResolveFooterHeight(
                rows,
                FooterHeightFor(width));
            var budget = Math.Max(0, rows - footerHeight - 2);
            var tail = sessionRenderer.Surface.SnapshotTail(budget, width);
            foreach (var line in tail)
            {
                _out.WriteLine(Tui.InteractiveLineReader.ResizeEchoLine(line, width));
            }

            // Pin the reopened composer to the bottom without scrolling: pad with
            // blank rows up to the box top via raw writes (kept out of the surface
            // history). Newlines only scroll past the last viewport row, so this
            // never grows the scrollback. PushToBottomAsync must stay out of this
            // path: it unconditionally emits newlines at the bottom row.
            if (!typeAheadLive)
            {
                var boxHeight = !string.IsNullOrWhiteSpace(lineReader.Hints)
                    ? Tui.ComposerBox.Height + 1
                    : Tui.ComposerBox.Height;
                var boxTop = Math.Max(1, rows - boxHeight + 1);
                var cursorRow = tail.Count + 1 + (altBufferActive ? Tui.TuiScreenLayout.Compute(width, rows).HeaderHeight : 0);
                for (var i = cursorRow; i < boxTop; i++)
                {
                    _out.WriteLine();
                }
            }

            _out.Flush();

            if (altBufferActive && typeAheadLive)
            {
                // A mid-turn recovery rebuild (repainted durable text) must restore the
                // region and the persistent footer before streaming continues.
                ApplyFooterLayout(repaint: true);
            }

            Tui.TuiDebug.Log(
                "layout",
                $"redraw rows={rows} width={width} tail={tail.Count} budget={budget} history={sessionRenderer.Surface.HistoryCount}");
        }

        /// <summary>Panel rows the AltBuffer footer must add above the composer, if any.</summary>
        List<string> PanelLines(int width)
        {
            if (!altBufferActive)
            {
                return [];
            }

            if (typeAhead.Approval is { } approval)
            {
                return [.. approval.RenderFloating(width)];
            }

            if (typeAhead.Choice is { } choice)
            {
                var panelWidth = Tui.TuiFloatingPanel.ComputeWidth(width);
                return [.. Tui.TuiFloatingPanel.Center(choice.List.Render(panelWidth), width)];
            }

            if (typeAhead.Secret is { } secret)
            {
                var panelWidth = Tui.TuiFloatingPanel.ComputeWidth(width);
                return [.. Tui.TuiFloatingPanel.Center(
                    Tui.KeyInputPanel.Compose(panelWidth, secret.ProviderDisplay ?? "api key", secret.Buffer.Length),
                    width)];
            }

            return [];
        }

        /// <summary>
        /// Footer rows for the live viewport: the spec's fixed three rows (status, live tool
        /// card, composer) in the AltBuffer frame, the historical dynamic footer otherwise.
        /// An open approval/trust/picker/secret panel temporarily grows the footer.
        /// </summary>
        int FooterHeightFor(int width) => altBufferActive
            ? Tui.TuiScreenLayout.Compute(width, Tui.TuiViewport.Height()).FooterHeight + PanelLines(width).Count
            : typeAhead.FooterHeight(width);

        /// <summary>
        /// Key-loop repaint: in the AltBuffer idle state, while the REPL handles a slash
        /// command (no pending request), the command's raw output is streaming into the
        /// transcript region and a footer paint would park the caret over it. The next
        /// idle read repaints the footer instead.
        /// </summary>
        void RepaintFooterForKeys()
        {
            if (altBufferActive && !typeAhead.TurnActive && typeAhead.IdleLine is null && !typeAhead.IsPanelActive)
            {
                return;
            }

            PaintStickyFooter();
        }

        void PaintStickyFooter()
        {
            if (!sticky.Active)
            {
                return;
            }

            var footerWidth = Tui.TuiViewport.Width();
            List<string> footer;
            int? caretRow;
            if (altBufferActive)
            {
                // Fixed three-row footer: status, live tool card, single-row composer.
                // Any approval/picker/secret panel sits between the tool row and the
                // composer and temporarily grows the footer beyond three rows.
                footer =
                [
                    sessionRenderer.Status.Compose(footerWidth),
                    Tui.TuiToolCard.RenderLiveRow(sessionRenderer.Status.Activity, footerWidth, Tui.TuiPalette.Level),
                ];
                footer.AddRange(PanelLines(footerWidth));
                footer.Add(Tui.ComposerBox.ComposeRow(
                    footerWidth,
                    typeAhead.State,
                    "❯ ",
                    typeAhead.Buffer,
                    typeAhead.Buffer.Length == 0 ? Tui.InteractiveLineReader.ComposerPlaceholder : string.Empty,
                    lineReader.Legend));

                // While the operator types in the footer composer, the hardware cursor
                // belongs there so IME popups follow the text; an open panel owns the
                // keyboard and needs no caret.
                caretRow = typeAheadLive && !typeAhead.IsPanelActive ? footer.Count - 1 : (int?)null;
            }
            else
            {
                footer = [sessionRenderer.Status.Compose(footerWidth)];
                if (typeAhead.Approval is { } approval)
                {
                    footer.AddRange(approval.Render(footerWidth));
                }

                footer.AddRange(Tui.ComposerBox.Compose(
                    footerWidth,
                    typeAhead.State,
                    "❯ ",
                    typeAhead.Buffer,
                    string.Empty,
                    lineReader.Legend,
                    typeAhead.Hint,
                    typeAhead.TopLabel));
                caretRow = typeAheadLive && !typeAhead.IsApprovalActive ? footer.Count - 1 : (int?)null;
            }

            // While the operator is typing, the hardware cursor belongs in the composer: IME
            // candidate windows otherwise open wherever the transcript left the cursor. The
            // legend is passed through so the caret is clamped left of it.
            var caretColumn = Tui.ComposerBox.CursorColumn(footerWidth, "❯ ", typeAhead.Buffer, lineReader.Legend);
            sticky.Paint(footer, footerWidth, caretRow, caretColumn);
        }

        // Every diagnostic line the REPL prints goes through here: in the AltBuffer frame
        // the footer paint parks the cursor on the composer row, and a raw stderr write
        // there smudges the frame and can scroll the pinned header away.
        Task WriteAltLineAsync(TextWriter writer, string line) =>
            WriteAltSafeAsync(sticky, altBufferActive, writer, line);

        /// <summary>
        /// Rebuilds the pinned layout for the viewport as it is right now, resetting any
        /// previous scrolling region first. Called at turn start and after a settled resize;
        /// when the window is too short for a pinned footer it leaves sticky mode cleanly so
        /// the transcript keeps scrolling normally.
        /// </summary>
        bool ApplyFooterLayout(bool repaint)
        {
            if (!stickyEnabled)
            {
                return false;
            }

            var rows = Tui.TuiViewport.Height();
            var width = Tui.TuiViewport.Width();
            var footerHeight = Tui.TuiStickyRegion.ResolveFooterHeight(rows, FooterHeightFor(width));
            if (footerHeight == 0)
            {
                sticky.Exit();
                sessionRenderer.Status.InFooter = false;
                sessionRenderer.LiveRowRepaintHook = null;
                return false;
            }

            var transcriptTop = TranscriptTopRow(rows);
            var applied = !sticky.Active
                ? sticky.Enter(rows, footerHeight, width, transcriptTop)
                : sticky.Rows == rows && sticky.Width == width && sticky.FooterHeight == footerHeight
                    ? true
                    : sticky.Reapply(rows, footerHeight, width, transcriptTop);

            if (!applied)
            {
                sessionRenderer.Status.InFooter = false;
                sessionRenderer.LiveRowRepaintHook = null;
                return false;
            }

            sessionRenderer.Status.InFooter = true;
            sessionRenderer.LiveRowRepaintHook = PaintStickyFooter;
            Tui.TuiDebug.Log(
                "layout",
                $"applied rows={rows} footer={footerHeight} first={sticky.FooterFirst} sticky={sticky.Active}");
            if (repaint)
            {
                PaintStickyFooter();
            }

            return true;
        }

        async Task StartTypeAheadAsync()
        {
            if (typeAheadLive || !sticky.Active || _in != Console.In || Console.IsInputRedirected)
            {
                return;
            }

            typeAheadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            typeAheadLive = true;
            typeAheadLoop = lineReader.RunTypeAheadAsync(
                typeAhead,
                RepaintFooterForKeys,
                onCancel: () => turnCts?.Cancel(),
                onExpand: () => sessionRenderer.ToggleExpand(),
                onResize: () =>
                {
                    // A settled resize: the terminal has reflowed the rows this layout was
                    // pinned to, so rebuild the whole screen instead of patching the frame.
                    Tui.TuiDebug.Log(
                        "layout",
                        $"onResize rows={Tui.TuiViewport.Height()} width={Tui.TuiViewport.Width()} wasRows={sticky.Rows}");
                    RedrawScreenForResize();
                    sessionRenderer.Width = Tui.TuiViewport.Width();
                    if (ApplyFooterLayout(repaint: true))
                    {
                        Tui.TuiDebug.Log("layout", "footer rebuilt after resize");
                    }
                },
                typeAheadCts.Token);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        async Task StopTypeAheadAsync()
        {
            if (typeAheadCts is null)
            {
                return;
            }

            typeAheadCts.Cancel();
            if (typeAheadLoop is not null)
            {
                try
                {
                    await typeAheadLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: the loop only ends when its token fires.
                }
            }

            typeAheadCts.Dispose();
            typeAheadCts = null;
            typeAheadLoop = null;
            typeAheadLive = false;
            typeAhead.Buffer = string.Empty;
            typeAhead.QueueRejected = false;
            typeAhead.Approval = null;
            typeAhead.Choice = null;
            typeAhead.Secret = null;
            typeAhead.IdleLine = null;
            typeAhead.TurnActive = false;
        }

        /// <summary>
        /// The AltBuffer picker: the floating panel takes the footer and the type-ahead key
        /// loop owns the keyboard. The sequential layout keeps the reader-based picker.
        /// </summary>
        async Task<string?> RequestChoicePanelAsync(Tui.ChoiceList list, CancellationToken ct)
        {
            var request = new Tui.PendingChoiceRequest(list);
            typeAhead.Choice = request;
            ApplyFooterLayout(repaint: false);
            PaintStickyFooter();
            using var registration = ct.Register(() => request.Decision.TrySetResult(null));
            try
            {
                return await request.Decision.Task.ConfigureAwait(false);
            }
            finally
            {
                typeAhead.Choice = null;
                ApplyFooterLayout(repaint: false);
                PaintStickyFooter();
            }
        }

        /// <summary>
        /// The AltBuffer masked secret panel: keys arrive through the type-ahead loop, the
        /// buffer never leaves the request object, and only its length reaches the screen.
        /// </summary>
        async Task<string?> RequestSecretPanelAsync(string? providerDisplay, CancellationToken ct)
        {
            var request = new Tui.PendingSecretRequest(providerDisplay);
            typeAhead.Secret = request;
            ApplyFooterLayout(repaint: false);
            PaintStickyFooter();
            using var registration = ct.Register(() => request.Decision.TrySetResult(null));
            try
            {
                return await request.Decision.Task.ConfigureAwait(false);
            }
            finally
            {
                typeAhead.Secret = null;
                ApplyFooterLayout(repaint: false);
                PaintStickyFooter();
            }
        }

        /// <summary>
        /// Reads one line from the persistent footer composer between turns. The type-ahead
        /// loop resolves the request on Enter; cancellation returns null so the REPL winds down.
        /// </summary>
        async Task<string?> ReadAltIdleLineAsync(CancellationToken ct)
        {
            var request = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            typeAhead.TurnActive = false;
            typeAhead.IdleLine = request;
            PaintStickyFooter();
            using var registration = ct.Register(() => request.TrySetResult(null));
            try
            {
                return await request.Task.ConfigureAwait(false);
            }
            finally
            {
                typeAhead.IdleLine = null;
            }
        }

        /// <summary>
        /// Answers one approval. Inside the pinned footer the choice list owns the keyboard;
        /// the sequential layout keeps the numbered stdin prompt. Cancelling the run releases
        /// the wait instead of leaving the frame callback blocked on an unanswered choice.
        /// </summary>
        async Task<string> RequestApprovalAsync(Tui.ApprovalPrompt prompt, CancellationToken approvalToken)
        {
            if (typeAheadLive && sticky.Active)
            {
                using var registration = approvalToken.Register(() => prompt.Decision.TrySetCanceled(approvalToken));
                typeAhead.Approval = prompt;
                ApplyFooterLayout(repaint: false);
                PaintStickyFooter();
                try
                {
                    return await prompt.Decision.Task.ConfigureAwait(false);
                }
                finally
                {
                    typeAhead.Approval = null;
                    ApplyFooterLayout(repaint: false);
                    PaintStickyFooter();
                }
            }

            await _out.WriteAsync(Tui.TuiPalette.BoldPaint("\n  ❯ Approve? [1: once / 2: deny] (default: 2): ")).ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
            var answer = await _in.ReadLineAsync(approvalToken).ConfigureAwait(false);
            var normalized = string.IsNullOrWhiteSpace(answer) ? "2" : answer.Trim().ToLowerInvariant();
            return normalized is "1" or "once" or "yes" or "y"
                ? Tui.ApprovalPrompt.AllowOnce
                : Tui.ApprovalPrompt.Deny;
        }

        /// <summary>
        /// Asks the operator to trust the workspace, in the composer, while the run holds
        /// the challenge open. No work is in flight at this point, so the type-ahead loop
        /// can hand the keyboard to the choice list — the same handoff a tool approval uses.
        /// Returning without a decision is a cancel, never a silent grant.
        /// </summary>
        async Task<string?> PromptWorkspaceTrustAsync(
            Tui.ApprovalPrompt prompt,
            CancellationToken trustToken)
        {
            if (!typeAheadLive || !sticky.Active)
            {
                return null;
            }

            using var registration = trustToken.Register(() => prompt.Decision.TrySetCanceled(trustToken));
            typeAhead.Approval = prompt;
            sessionRenderer.Status.SetState(Tui.TuiRunState.WaitingTrust);
            sessionRenderer.Status.SetDetail(prompt.Summary);
            ApplyFooterLayout(repaint: false);
            PaintStickyFooter();
            try
            {
                return await prompt.Decision.Task.ConfigureAwait(false);
            }
            finally
            {
                typeAhead.Approval = null;
                sessionRenderer.Status.SetDetail(null);
                // The turn either continues into the run or is about to be wound down;
                // either way the row must stop claiming it is waiting on trust.
                sessionRenderer.Status.SetState(Tui.TuiRunState.Thinking);
                ApplyFooterLayout(repaint: false);
                PaintStickyFooter();
            }
        }

        async Task<bool> PostTrustDecisionAsync(
            string runId,
            string trustRequestId,
            string displayHash,
            CancellationToken trustToken)
        {
            try
            {
                var trustResponse = await _agentClient.TrustAsync(
                        runId,
                        new TrustAgentRunRequest(trustRequestId, displayHash, "approve"),
                        trustToken)
                    .ConfigureAwait(false);
                if (string.Equals(trustResponse.RunId, runId, StringComparison.Ordinal)
                    && string.Equals(trustResponse.TrustRequestId, trustRequestId, StringComparison.Ordinal)
                    && RunStartCore.IsAcceptedTrustStatus(trustResponse.Status))
                {
                    return true;
                }

                await WriteAltLineAsync(_err, "workspace_trust_error: trust response does not match the challenge.").ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentApiException ex)
            {
                await WriteAltLineAsync(_err, $"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                return false;
            }
            catch (Exception ex)
            {
                await WriteAltLineAsync(_err, $"workspace_trust_error: {ex.Message}").ConfigureAwait(false);
                return false;
            }
        }

        /// <summary>
        /// Releases a run the operator declined to trust. The challenge cannot be answered
        /// later — it is single-use and the workspace is not on the allow list — so leaving
        /// the run parked would hold the workspace's active-run lock for the whole sweep
        /// interval. Trust is a pre-execution gate, so cancelling rolls back no tool effect.
        /// </summary>
        async Task CancelUntrustedRunAsync(string runId, CancellationToken trustToken)
        {
            try
            {
                await _agentClient.CancelAsync(runId, trustToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await WriteAltLineAsync(_err, $"run_cancel_failed: {ex.Message}").ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Runs the in-composer workspace-trust ask end to end: validate the binding, prompt,
        /// then post the server-issued challenge. A decline cancels the run, because the
        /// challenge is single-use and the run would otherwise sit on the workspace's
        /// active-run lock until the host swept it.
        /// </summary>
        async Task<(bool Trusted, bool Declined)> ResolveWorkspaceTrustInPlaceAsync(
            CreateAgentRunResponse response,
            string expectedSessionId,
            string expectedPreset,
            string workspacePath,
            bool json,
            CancellationToken trustToken)
        {
            if (!RunStartCore.IsCreateResponseBound(response, expectedSessionId, expectedPreset))
            {
                await WriteAltLineAsync(_err, "agent_create_failed: invalid create response binding.").ConfigureAwait(false);
                return (false, false);
            }

            var trustRequestId = response.TrustRequestId;
            var displayHash = response.DisplayIdentityHash;
            if (string.IsNullOrWhiteSpace(trustRequestId) || string.IsNullOrWhiteSpace(displayHash))
            {
                await WriteAltLineAsync(_err, "workspace_trust_error: trust challenge is incomplete.").ConfigureAwait(false);
                return (false, false);
            }

            var decision = await PromptWorkspaceTrustAsync(
                    Tui.ApprovalPrompt.ForWorkspaceTrust(workspacePath),
                    trustToken)
                .ConfigureAwait(false);

            if (decision is null)
            {
                // No composer to ask in, so there is no frame to protect and the bounded
                // code keeps its established stream. Never read stdin here: the type-ahead
                // loop may already own it, which is how the old prompt hung forever.
                // --json owns stdout, so the human notice lands on stderr there.
                await (json ? _err : _out).WriteLineAsync(
                        $"workspace_trust_required: rerun with --trust-workspace or use 'ru agent trust {response.RunId} --approve'.")
                    .ConfigureAwait(false);
                return (false, false);
            }

            if (!string.Equals(decision, Tui.ApprovalPrompt.TrustApprove, StringComparison.Ordinal))
            {
                await CancelUntrustedRunAsync(response.RunId, trustToken).ConfigureAwait(false);
                return (false, true);
            }

            var granted = await PostTrustDecisionAsync(response.RunId, trustRequestId, displayHash, trustToken)
                .ConfigureAwait(false);
            return (granted, false);
        }

        /// <summary>
        /// Reprints the part of the answer the deltas never carried.
        ///
        /// The live event hub has no replay buffer: a delta published while the client is
        /// still subscribing is dropped, and because deltas are ordered what is lost is the
        /// head of the reply. The run's own durable <c>assistanttext</c> payload is complete,
        /// so the gap is recoverable rather than lost. This mirrors what the WinUI surface
        /// already does by reloading session history when a run ends.
        ///
        /// Only a suffix is reprinted, and only when the streamed text is a true prefix of
        /// the payload. Anything else -- a shorter payload, a rewritten answer, a payload
        /// that is not assistant text -- is left alone rather than guessed at, because a
        /// terminal cannot unprint.
        /// </summary>
        async Task RecoverMissingAssistantTextAsync(
            string runId,
            List<string> payloadRefs,
            CancellationToken recoveryToken)
        {
            if (payloadRefs.Count == 0)
            {
                Tui.TuiDebug.Log("recover", "no step payload refs seen on the stream");
                return;
            }

            var expected = new StringBuilder();
            try
            {
                foreach (var payloadRef in payloadRefs)
                {
                    // GetPayloadAsync unwraps the host's envelope and returns the assistant
                    // text itself, so there is nothing to parse here -- this is prose, and
                    // parsing it as JSON throws.
                    var text = await _agentClient.GetPayloadAsync(runId, payloadRef, recoveryToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(text))
                    {
                        expected.Append(text);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await WriteAltLineAsync(_err, $"assistant_text_recovery_failed: {ex.Message}").ConfigureAwait(false);
                return;
            }

            if (expected.Length == 0)
            {
                Tui.TuiDebug.Log("recover", $"no durable text behind {payloadRefs.Count} refs");
                return;
            }

            var full = expected.ToString();
            var streamed = sessionRenderer.Transcript.StreamedAssistantText;
            Tui.TuiDebug.Log("recover", $"streamed {streamed.Length} of {full.Length} chars, {sessionRenderer.Transcript.AssistantBlockRowCount} rows recorded, suppress={sessionRenderer.Surface.SuppressRepaints}");
            var decision = Tui.AssistantTextReconciler.Decide(streamed, full);
            if (decision == Tui.AssistantTextRecovery.Complete
                || decision == Tui.AssistantTextRecovery.None)
            {
                return;
            }

            if (decision == Tui.AssistantTextRecovery.ReportTruncation)
            {
                // A terminal cannot unprint, so the head cannot be appended after the tail.
                // Rewrite the turn's block from the run's own text and rebuild the screen
                // from the surface history, which is the path a resize already uses.
                if (sessionRenderer.Transcript.RepaintAssistantText(full))
                {
                    RedrawScreenForResize();
                    Tui.TuiDebug.Log("recover", $"repainted turn from durable text ({full.Length} chars)");
                    return;
                }

                await WriteAltLineAsync(
                        _err,
                        $"assistant_text_truncated: {streamed.Length} of {full.Length} characters reached this terminal. The full reply is in the session; /status or reopen it to read all of it.")
                    .ConfigureAwait(false);
                Tui.TuiDebug.Log("recover", $"head missing: {streamed.Length} of {full.Length}");
                return;
            }

            var missing = Tui.AssistantTextReconciler.SuffixFor(decision, streamed, full);
            if (missing.Length == 0)
            {
                return;
            }

            // The tracked append point already continues the streamed row; rewinding to
            // the row start would overwrite it.
            sessionRenderer.Transcript.AppendAssistant(missing);
            sessionRenderer.Flush();
            Tui.TuiDebug.Log("recover", $"reprinted {missing.Length} chars of {full.Length}");
        }

        async Task FinishTurnAsync()
        {
            if (altBufferActive)
            {
                // Between turns the AltBuffer frame persists: pinned header, scrolling
                // transcript region and fixed footer all stay in place, and the composer
                // becomes the REPL's own prompt. The type-ahead loop keeps running so the
                // next line arrives from the footer instead of the sequential reader box.
                queuedLine ??= pendingPrompt.Take();
                Tui.TuiDebug.Log("layout", $"finish turn alt rows={Tui.TuiViewport.Height()} first={sticky.FooterFirst}");
                typeAhead.TurnActive = false;
                sessionRenderer.EndTurn();
                turnCts?.Dispose();
                turnCts = null;
                if (!sticky.Active)
                {
                    // A durable-text recovery rebuild tears the region down; restore it so
                    // the footer and the next idle prompt land inside the frame.
                    ApplyFooterLayout(repaint: false);
                }

                PaintStickyFooter();
                return;
            }

            await StopTypeAheadAsync().ConfigureAwait(false);
            queuedLine ??= pendingPrompt.Take();
            Tui.TuiDebug.Log("layout", $"finish turn rows={Tui.TuiViewport.Height()} first={sticky.FooterFirst}");
            sessionRenderer.LiveRowRepaintHook = null;
            sessionRenderer.Status.InFooter = false;
            sticky.Exit();
            sessionRenderer.EndTurn();
            turnCts?.Dispose();
            turnCts = null;
        }

        async Task UpdateLegendAsync()
        {
            try
            {
                var s = await _settingsClient.GetAsync(cancellationToken).ConfigureAwait(false);
                var bName = s.TryGetProperty("backend", out var bProp) && !string.IsNullOrWhiteSpace(bProp.GetString())
                    ? bProp.GetString()!
                    : backendName;
                var curModel = bName switch
                {
                    "OpenAI" => s.TryGetProperty("openai_model", out var oam) ? oam.GetString() ?? "" : "",
                    "Anthropic" => s.TryGetProperty("anthropic_model", out var anm) ? anm.GetString() ?? "" : "",
                    _ => s.TryGetProperty("model", out var mProp) ? mProp.GetString() ?? "" : "",
                };
                string effort = ReadReasoningEffort(s);

                string mLbl = string.IsNullOrWhiteSpace(curModel) ? (string.IsNullOrWhiteSpace(activeModel) ? "model" : activeModel) : curModel;
                lineReader.Legend = string.IsNullOrWhiteSpace(effort) ? mLbl : $"{mLbl} · {effort}";
            }
            catch (Exception)
            {
                // Live refresh failed; legend keeps the last known values.
                string mLbl = string.IsNullOrWhiteSpace(activeModel) ? "model" : activeModel;
                lineReader.Legend = string.IsNullOrWhiteSpace(reasoningEffort) ? mLbl : $"{mLbl} · {reasoningEffort}";
            }
        }

        long? lastTotalTokens = null;
        _slashCommands.SnapshotProvider = () => new Tui.TuiStatusSnapshot(
            SessionId: sessionId,
            Preset: cmd.Preset,
            Backend: backendName,
            Model: activeModel,
            Workspace: canonicalWorkspace,
            Turn: turn,
            TotalTokens: lastTotalTokens,
            ToolsSettled: sessionRenderer.Transcript.ToolsSettled,
            ApiKeyConfigured: null);
        await UpdateLegendAsync().ConfigureAwait(false);
        if (altBufferActive)
        {
            // Enter the transcript region before the landing/replay write so even a long
            // replay scrolls under the pinned header instead of off the screen; the footer
            // is painted after the content below.
            ApplyFooterLayout(repaint: false);
        }

        if (cmd.Json || string.IsNullOrWhiteSpace(cmd.SessionId))
        {
            RenderLandingScreen();
        }
        else
        {
            // A reopened session shows where the conversation stopped; a fresh session
            // (or one whose replay cannot be read) keeps the landing screen.
            var replay = await LoadSessionReplayAsync(sessionId).ConfigureAwait(false);
            if (replay.Messages.Count > 0)
            {
                landingScreenVisible = false;
                PaintAltHeader();
                RenderSessionReplay(sessionId, replay);
            }
            else
            {
                RenderLandingScreen();
                if (replay.Error is not null)
                {
                    sessionRenderer.Note(
                        $"session_replay_unavailable: {replay.Error}",
                        Tui.TuiToken.Warn);
                    sessionRenderer.Flush();
                }
            }
        }

        if (modelWarning is not null)
        {
            if (cmd.Json)
            {
                // Nothing human may land on the NDJSON stdout.
                await WriteAltLineAsync(_err, modelWarning).ConfigureAwait(false);
            }
            else
            {
                sessionRenderer.Note(modelWarning, Tui.TuiToken.Warn);
                sessionRenderer.Flush();
            }
        }

        if (!string.IsNullOrWhiteSpace(cmd.SessionId))
        {
            // Only an explicit --session reopens someone else's session; a fresh session
            // has no run to adopt and must not spend the round trip.
            await AdoptSessionRunAsync(sessionId).ConfigureAwait(false);
        }

        if (altBufferActive)
        {
            // The persistent idle frame: header pinned, transcript region installed and
            // the footer composer owning the keyboard before the first prompt.
            PaintAltHeader();
            ApplyFooterLayout(repaint: true);
            await StartTypeAheadAsync().ConfigureAwait(false);
        }

        string prompt = Tui.TuiPalette.BoldPaint("❯ ");
        string contPrompt = Tui.TuiPalette.DimPaint("    ... ❯ ");

        /// <summary>
        /// Follows one run to its terminal frame, rendering every durable and transient
        /// event and recovering a lost assistant head once it ends. <paramref name="afterSequence"/>
        /// is the durable cursor to resume from: an interactive turn starts at 0, while a
        /// continued run starts at the parked run's LastSequence so the resume never
        /// replays output the terminal already printed.
        /// </summary>
        async Task FollowRunAsync(string runId, long afterSequence, CancellationToken ct)
        {
            async Task OnFrameAsync(SseFrame frame, CancellationToken frameCt)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A parked run emits no further driving frames; the server holds the SSE
                // stream open, which used to read as an eternally spinning "Thinking" row.
                // Release the stream instead: the catch treats a set parkedRunId as a
                // pause, not a user interruption, so no cancel is posted.
                if (TryReadParkedTransition(frame, out var parkedRun))
                {
                    parkedRunId = parkedRun;
                    turnCts?.Cancel();
                }

                if (cmd.Json)
                {
                    NdjsonEventRenderer.RenderFrame(frame, _out);

                    // --json never prompts for an approval: there is no composer to ask in,
                    // and reading stdin here would consume the scripted consumer's next
                    // prompt. The run stays waiting server-side and the operator answers
                    // out of band, so this notice goes to stderr and the streaming turn
                    // ends instead of spinning on a stream that will not advance.
                    if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"approval.requested\"", StringComparison.Ordinal))
                    {
                        HarnessSafeEventEnvelope? approvalEnvelope = null;
                        try
                        {
                            var candidate = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                            if (candidate is { EventType: "approval.requested" } && !string.IsNullOrEmpty(candidate.ApprovalId))
                            {
                                approvalEnvelope = candidate;
                            }
                        }
                        catch (JsonException)
                        {
                            // Non-conforming frame payload; the NDJSON line above already
                            // carried the event and there is no id to point the operator at.
                        }

                        if (approvalEnvelope is not null)
                        {
                            var targetRunId = !string.IsNullOrEmpty(approvalEnvelope.RunId) ? approvalEnvelope.RunId : runId;
                            await WriteAltLineAsync(
                                    _err,
                                    $"approval_required: run {targetRunId} is waiting for approval {approvalEnvelope.ApprovalId}. Answer it with 'ru agent approve {targetRunId} {approvalEnvelope.ApprovalId}' or deny it with 'ru agent deny {targetRunId} {approvalEnvelope.ApprovalId}'.")
                                .ConfigureAwait(false);
                            await _out.FlushAsync().ConfigureAwait(false);
                            await _err.FlushAsync().ConfigureAwait(false);
                            throw new RunWaitingApprovalException(targetRunId, approvalEnvelope.ApprovalId!);
                        }
                    }
                }
                else
                {
                    // No per-frame cursor restore: the surface tracks the transcript
                    // append point across footer repaints, and rewinding to the row
                    // start here would overwrite a partially streamed row.
                    sessionRenderer.RenderFrame(frame);
                    if (sticky.Active && typeAheadLive)
                    {
                        PaintStickyFooter();
                    }

                    if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"approval.requested\"", StringComparison.Ordinal))
                    {
                        try
                        {
                            var envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                            if (envelope is not null && envelope.EventType == "approval.requested" && !string.IsNullOrEmpty(envelope.ApprovalId))
                            {
                                var targetRunId = !string.IsNullOrEmpty(envelope.RunId) ? envelope.RunId : runId;
                                var decision = await RequestApprovalAsync(Tui.ApprovalPrompt.For(envelope.SafeSummary), frameCt).ConfigureAwait(false);

                                if (decision == Tui.ApprovalPrompt.Deny)
                                {
                                    await _agentClient.DenyAsync(targetRunId, envelope.ApprovalId, frameCt).ConfigureAwait(false);
                                    sessionRenderer.Note("denied · this tool call aborts", Tui.TuiToken.Err);
                                }
                                else
                                {
                                    await _agentClient.ApproveAsync(targetRunId, envelope.ApprovalId, decision, frameCt).ConfigureAwait(false);
                                    var scope = decision == Tui.ApprovalPrompt.AllowDirectory ? "folder for this session"
                                        : decision == Tui.ApprovalPrompt.AllowAlways ? "remembered for this project"
                                        : "once";
                                    sessionRenderer.Note("approved · " + scope, Tui.TuiToken.Ok);
                                }

                                await _out.FlushAsync().ConfigureAwait(false);
                            }
                        }
                        catch (OperationCanceledException) when (frameCt.IsCancellationRequested)
                        {
                            // The operator cancelled the turn while the approval was open.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            await WriteAltLineAsync(_err, $"inline_approval_error: {ex.Message}").ConfigureAwait(false);
                        }
                    }

                    if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"model.step_completed\"", StringComparison.Ordinal))
                    {
                        // Remember every step's durable assistant payload so a lost
                        // delta tail can be recovered once the run finishes.
                        try
                        {
                            var stepEnvelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                            if (stepEnvelope is { EventType: "model.step_completed" }
                                && !string.IsNullOrWhiteSpace(stepEnvelope.PayloadRef))
                            {
                                assistantPayloadRefs.Add(stepEnvelope.PayloadRef);
                            }
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            // Non-conforming frame payload; the recovery falls back to
                            // whatever steps were already recorded.
                        }
                    }

                    if (!frame.IsTransient && frame.Data is not null && frame.Data.Contains("\"total_tokens\"", StringComparison.Ordinal))
                    {
                        try
                        {
                            var usageEnvelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
                            if (usageEnvelope?.TotalTokens is { } total)
                            {
                                lastTotalTokens = total;
                                sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
                            }
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            // Non-conforming frame payload; token tally keeps its last value.
                        }
                    }
                }
                await _out.FlushAsync().ConfigureAwait(false);
            }

            await StreamRunEventsResumedAsync(runId, OnFrameAsync, ct, afterSequence).ConfigureAwait(false);
            sessionRenderer.Flush();
            await RecoverMissingAssistantTextAsync(runId, assistantPayloadRefs, ct).ConfigureAwait(false);
            await _out.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Shared wind-down for a cancelled turn: distinguishes a user interruption (the
        /// server run is cancelled so it releases the workspace lock) from a run that
        /// parked itself (nothing is executing; the user resumes it explicitly).
        /// </summary>
        async Task ReportInterruptionAsync()
        {
            var userCancelled = turnCts?.IsCancellationRequested == true;
            await FinishTurnAsync().ConfigureAwait(false);
            // Only the run actually being followed may be cancelled. An earlier park can
            // still be tracked (parkedRunId is kept for bare Continue when the user resumes
            // a different run), so "some run is parked" must not suppress the cancel for a
            // different active run.
            var cancelRunId = userCancelled
                && activeRunId is not null
                && !string.Equals(activeRunId, parkedRunId, StringComparison.Ordinal)
                    ? activeRunId
                    : null;
            if (cancelRunId is null)
            {
                if (parkedRunId is null)
                {
                    await humanOut.WriteLineAsync(Tui.TuiPalette.Warn("Run interrupted by user.")).ConfigureAwait(false);
                }

                // A parked run is the user's to resume: there is nothing to cancel, and the
                // transcript already printed the single "run parked ... /continue" notice
                // for it, so printing another line here only duplicated that guidance.
                return;
            }

            try
            {
                await _agentClient.CancelAsync(cancelRunId, CancellationToken.None).ConfigureAwait(false);
                await humanOut.WriteLineAsync(Tui.TuiPalette.Warn($"Run interrupted by user · cancelled {cancelRunId}.")).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await humanOut.WriteLineAsync(Tui.TuiPalette.Warn("Run interrupted by user.")).ConfigureAwait(false);
                await humanOut.WriteLineAsync(Tui.TuiPalette.Err($"run_cancel_failed: {ex.Message}")).ConfigureAwait(false);
                await humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint($"run {cancelRunId} may still be running; release it with 'ru agent cancel {cancelRunId}'.")).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resumes a parked run the way the GUI's typed Continue does: read the parked
        /// run's durable cursor, post the continue, then follow the run from that cursor
        /// as if it were a fresh turn. Without a parked run and without an explicit run
        /// id this is informational, never an error.
        /// </summary>
        async Task ContinueParkedRunAsync(string? runIdArg)
        {
            var targetRunId = string.IsNullOrWhiteSpace(runIdArg) ? parkedRunId : runIdArg.Trim();
            if (string.IsNullOrWhiteSpace(targetRunId))
            {
                await humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint("no parked run · nothing to continue")).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            // The parked turn already rendered durable frames up to LastSequence; the
            // continue call's own "continued" event is the first frame after it. Resuming
            // from 0 would replay the whole turn into a terminal that cannot unprint.
            GetAgentRunResponse run;
            try
            {
                run = await _agentClient.GetRunAsync(targetRunId, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentApiException ex)
            {
                await WriteAltLineAsync(_err, Tui.TuiPalette.Err(CliErrorText.Format(ex))).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                // A transport failure must not escape the REPL: report it bounded and keep
                // the prompt (and the parked tracking) intact.
                await WriteAltLineAsync(_err, Tui.TuiPalette.Err(
                    $"continue_error: {CliErrorText.ShortMessage(ex.Message)}")).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            if (run.TerminalAt is not null || IsTerminalRunStatus(run.Status))
            {
                await WriteAltLineAsync(_err, Tui.TuiPalette.Err(
                    $"run_not_runnable: run {targetRunId} is {run.Status} and cannot be continued.")).ConfigureAwait(false);
                if (string.Equals(parkedRunId, targetRunId, StringComparison.Ordinal))
                {
                    parkedRunId = null;
                }

                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            // An explicit target that is not parked (running, waiting_approval,
            // waiting_workspace_trust) is being driven elsewhere or waits on another
            // decision: the resume POST could only earn a 409. Bare Continue targets
            // the tracked parked run and keeps its existing behavior.
            if (runIdArg is not null && !IsParkedRunStatus(run.Status))
            {
                await WriteAltLineAsync(_err, Tui.TuiPalette.Err(
                    $"run_not_parked: run {targetRunId} is {run.Status} and not waiting to continue.")).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sessionRenderer.Width = Tui.TuiViewport.Width();
            sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
            typeAhead.TurnActive = true;
            PaintAltHeader();
            if (ApplyFooterLayout(repaint: true))
            {
                await StartTypeAheadAsync().ConfigureAwait(false);
            }

            if (sticky.Active)
            {
                sticky.RestoreTranscriptCursor();
            }

            sessionRenderer.BeginTurn();
            activeRunId = targetRunId;
            var wasParkedRun = string.Equals(parkedRunId, targetRunId, StringComparison.Ordinal);
            assistantPayloadRefs.Clear();

            try
            {
                await _agentClient.ContinueAsync(targetRunId, turnCts.Token).ConfigureAwait(false);
                if (wasParkedRun)
                {
                    // Only a confirmed continue drops the parked tracking: a rejected POST
                    // must leave bare "Continue" working for the same run. The run is
                    // moving again now, so the follow's cancel guard may target it.
                    parkedRunId = null;
                }

                await FollowRunAsync(targetRunId, run.LastSequence, turnCts.Token).ConfigureAwait(false);
                await FinishTurnAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReportInterruptionAsync().ConfigureAwait(false);
            }
            catch (RunWaitingApprovalException)
            {
                // The --json approval escape: the notice is already on stderr and the
                // run stays waiting server-side. Wind the turn down and let the caller
                // exit the chat instead of settling for a "continue_error" line.
                await FinishTurnAsync().ConfigureAwait(false);
                throw;
            }
            catch (AgentApiException ex)
            {
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteAltLineAsync(_err, Tui.TuiPalette.Err(CliErrorText.Format(ex))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteAltLineAsync(_err, Tui.TuiPalette.Err($"continue_error: {ex.Message}")).ConfigureAwait(false);
            }

            await _out.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Loads the bounded replay for a session reopened with --session. The wire payload
        /// (POST /sessions/{id}/activate) carries user/ai text only -- tool rows and thinking
        /// belong to runs and never travel in the session document -- so only those roles are
        /// kept. The newest <see cref="SessionReplayMessageLimit"/> messages are considered and
        /// each kept message is capped at <see cref="SessionReplayMaxChars"/> characters.
        /// Fail-soft: a read fault returns the bounded one-line reason and no messages.
        /// </summary>
        async Task<(List<(string Role, string Text)> Messages, int Hidden, int Shortened, string? Error)>
            LoadSessionReplayAsync(string targetSessionId)
        {
            JsonElement root;
            try
            {
                root = await _sessionClient.GetSessionAsync(targetSessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ([], 0, 0, CliErrorText.ShortMessage(ex.Message));
            }

            var all = new List<(string Role, string Text)>();
            if (root.TryGetProperty("session", out var sessionElement)
                && sessionElement.ValueKind == JsonValueKind.Object
                && sessionElement.TryGetProperty("messages", out var messagesElement)
                && messagesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messagesElement.EnumerateArray())
                {
                    var role = message.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
                        ? roleElement.GetString()!.Trim().ToLowerInvariant()
                        : string.Empty;
                    var content = message.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
                        ? contentElement.GetString() ?? string.Empty
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(content))
                    {
                        continue;
                    }

                    all.Add((role, content.Trim()));
                }
            }

            var tail = all.Count > SessionReplayMessageLimit
                ? all.GetRange(all.Count - SessionReplayMessageLimit, SessionReplayMessageLimit)
                : all;

            var messages = new List<(string Role, string Text)>(tail.Count);
            var hidden = 0;
            var shortened = 0;
            foreach (var (role, text) in tail)
            {
                if (role is not ("user" or "ai" or "assistant"))
                {
                    // Tool/thinking/other rows are not replayed; the count is marked once.
                    hidden++;
                    continue;
                }

                var bounded = text;
                if (bounded.Length > SessionReplayMaxChars)
                {
                    bounded = bounded[..SessionReplayMaxChars].TrimEnd() + " …";
                    shortened++;
                }

                messages.Add((role, bounded));
            }

            return (messages, hidden, shortened, null);
        }

        /// <summary>
        /// Reprints the loaded replay through the same renderer a live turn uses: the prompt
        /// echo for user rows and the streaming/markdown assistant path for replies. Nothing
        /// is invented here, so a replay row reads exactly like the row the turn produced.
        /// </summary>
        void RenderSessionReplay(
            string targetSessionId,
            (List<(string Role, string Text)> Messages, int Hidden, int Shortened, string? Error) replay)
        {
            sessionRenderer.Note(
                $"↺ replayed the last {replay.Messages.Count} messages from session {targetSessionId}",
                Tui.TuiToken.Meta);
            foreach (var (role, text) in replay.Messages)
            {
                if (string.Equals(role, "user", StringComparison.Ordinal))
                {
                    sessionRenderer.Surface.WriteLine(
                        Tui.InteractiveLineReader.BuildSubmitEcho(text, width: sessionRenderer.Width));
                }
                else
                {
                    sessionRenderer.Transcript.AppendAssistant(text);
                    sessionRenderer.Flush();
                    // The streaming path leaves the reply row open for the next delta;
                    // a replay message is final, so the row must end before the next one.
                    sessionRenderer.Surface.Line();
                }
            }

            if (replay.Shortened > 0 || replay.Hidden > 0)
            {
                var parts = new List<string>();
                if (replay.Shortened > 0)
                {
                    parts.Add($"{replay.Shortened} shortened");
                }

                if (replay.Hidden > 0)
                {
                    parts.Add($"{replay.Hidden} tool/other rows hidden");
                }

                sessionRenderer.Note("… " + string.Join(", ", parts), Tui.TuiToken.Meta);
            }

            sessionRenderer.Flush();
        }

        /// <summary>
        /// Takes over the session's newest run when the session was reopened with
        /// --session. After a host restart a parked run is invisible otherwise: the CLI
        /// claims no parked run, so bare Continue would send an ordinary prompt and the
        /// run would sit parked forever. The newest run only is adopted, matching the
        /// GUI. Fail-soft: a read fault prints nothing and never breaks the REPL start.
        /// </summary>
        async Task AdoptSessionRunAsync(string targetSessionId)
        {
            try
            {
                var runIds = await _agentClient
                    .ListSessionRunIdsAsync(targetSessionId, cancellationToken)
                    .ConfigureAwait(false);
                if (runIds.Count == 0)
                {
                    return;
                }

                // The client reverses the wire's newest-first order, so the tail is newest.
                var newestRunId = runIds[^1];
                var run = await _agentClient.GetRunAsync(newestRunId, cancellationToken).ConfigureAwait(false);
                if (run.TerminalAt is not null || IsTerminalRunStatus(run.Status))
                {
                    return;
                }

                if (IsParkedRunStatus(run.Status))
                {
                    // Bare Continue and /continue act on this run immediately: the parked
                    // path the operator would otherwise have to discover by run id.
                    parkedRunId = newestRunId;
                    var notice = Tui.TuiTranscript.FormatParkedNotice(newestRunId, run.Status);
                    if (cmd.Json)
                    {
                        await WriteAltLineAsync(_err, Tui.TuiPalette.Warn(notice)).ConfigureAwait(false);
                    }
                    else
                    {
                        sessionRenderer.Note(notice, Tui.TuiToken.Warn);
                    }
                }
                else if (string.Equals(run.Status, "running", StringComparison.OrdinalIgnoreCase))
                {
                    // An in-flight run is only pointed at: following it here would hijack
                    // the composer with output the operator did not ask to watch.
                    var notice = Tui.TuiTranscript.FormatAttachNotice(newestRunId);
                    if (cmd.Json)
                    {
                        await WriteAltLineAsync(_err, Tui.TuiPalette.DimPaint(notice)).ConfigureAwait(false);
                    }
                    else
                    {
                        sessionRenderer.Note(notice, Tui.TuiToken.Meta);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Adoption is best effort: an unreachable list/read must not break the
                // REPL start, and an explicit '/continue <run-id>' still works.
                Tui.TuiDebug.Log("adopt", $"session run adoption failed: {CliErrorText.ShortMessage(ex.Message)}");
            }
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
        {
            // A prompt typed while the previous turn streamed is sent without a second Enter.
            // In the AltBuffer frame the composer is the persistent footer, so idle input
            // comes from the type-ahead loop; the sequential reader only serves fallbacks.
            string? line;
            if (queuedLine is not null)
            {
                line = queuedLine;
            }
            else if (altBufferActive && typeAheadLive)
            {
                line = await ReadAltIdleLineAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                line = await lineReader.ReadLineWithContinuationAsync(_in, humanOut, prompt, contPrompt, cancellationToken).ConfigureAwait(false);
            }

            queuedLine = null;
            if (line is null) break;
            line = line.Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(line)) continue;

            if (altBufferActive && sticky.Active)
            {
                // Idle output (slash listings, command panels) flows into the scrolling
                // transcript region; the pinned header and the footer must not move. The
                // next footer paint (each read) restores the composer row afterwards.
                sticky.RestoreTranscriptCursor();
            }

            if (line.StartsWith("/", StringComparison.Ordinal))
            {
                if (landingScreenVisible && !line.StartsWith("/clear", StringComparison.OrdinalIgnoreCase))
                {
                    landingScreenVisible = false;
                }
                if (!cmd.Json)
                {
                    await _out.WriteLineAsync(Tui.InteractiveLineReader.BuildSubmitEcho(line, width: Tui.TuiViewport.Width())).ConfigureAwait(false);
                }
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var slashCmd = parts[0].ToLowerInvariant();
                var arg = parts.Length > 1 ? parts[1].Trim() : null;

                if (slashCmd is "/exit" or "/quit")
                {
                    await humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint("Exiting interactive terminal. Goodbye!")).ConfigureAwait(false);
                    await humanOut.FlushAsync().ConfigureAwait(false);
                    return 0;
                }
                if (slashCmd is "/history")
                {
                    var history = lineReader.History;
                    if (history.Count == 0)
                    {
                        await humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint("  (No prompt history yet)")).ConfigureAwait(false);
                    }
                    else
                    {
                        await humanOut.WriteLineAsync(Tui.TuiPalette.BoldPaint("Prompt History:")).ConfigureAwait(false);
                        for (int i = 0; i < history.Count; i++)
                        {
                            await humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint($"  {i + 1,3}: ") + history[i]).ConfigureAwait(false);
                        }
                    }
                    await humanOut.FlushAsync().ConfigureAwait(false);
                    continue;
                }
                if (slashCmd is "/clear")
                {
                    turn = 0;
                    landingScreenVisible = true;
                    lastTotalTokens = null;
                    if (altBufferActive)
                    {
                        // Reset in place: region off, alt screen wiped, header repainted by
                        // the landing render, footer restored by ApplyFooterLayout.
                        sticky.Exit();
                        sessionRenderer.Status.InFooter = false;
                        sessionRenderer.Surface.Repaint(writer => writer.Write(FullRebuildClearSequence(altBufferActive)));
                    }
                    else if (!cmd.Json && _out == Console.Out && !Console.IsOutputRedirected)
                    {
                        try { Console.Clear(); _out.Write("\u001b[3J\u001b[H"); } catch (IOException) { /* No live console to clear. */ }
                    }
                    RenderLandingScreen();
                    if (altBufferActive && typeAheadLive)
                    {
                        ApplyFooterLayout(repaint: true);
                    }
                    await humanOut.FlushAsync().ConfigureAwait(false);
                    continue;
                }
                if (await _slashCommands.TryHandleAsync(slashCmd, arg, sessionId, cmd.Preset, canonicalWorkspace, cancellationToken).ConfigureAwait(false))
                {
                    await UpdateLegendAsync().ConfigureAwait(false);
                    continue;
                }
                if (slashCmd is "/rewind")
                {
                    if (arg is null || !int.TryParse(arg, out var toIndex))
                    {
                        await WriteAltLineAsync(_err, "usage: /rewind <message_index>").ConfigureAwait(false);
                        continue;
                    }
                    try
                    {
                        await _sessionClient.RewindAsync(sessionId, toIndex, cancellationToken).ConfigureAwait(false);
                        await humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Rewound session {sessionId} to message index {toIndex}")).ConfigureAwait(false);
                    }
                    catch (AgentApiException ex)
                    {
                        await WriteAltLineAsync(_err, $"{CliErrorText.Format(ex)}").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await WriteAltLineAsync(_err, $"rewind_error: {ex.Message}").ConfigureAwait(false);
                    }
                    await _out.FlushAsync().ConfigureAwait(false);
                    continue;
                }
                if (slashCmd is "/continue")
                {
                    try
                    {
                        await ContinueParkedRunAsync(arg).ConfigureAwait(false);
                    }
                    catch (RunWaitingApprovalException)
                    {
                        // Same escape as a fresh turn: an approval under --json ends the
                        // chat rather than reading a prompt it must not answer.
                        return 0;
                    }
                    continue;
                }
                await WriteAltLineAsync(_err, $"unknown_command: {slashCmd}. Type /help for assistance.").ConfigureAwait(false);
                continue;
            }

            // The GUI accepts a bare typed "Continue" while a run is parked. Only claim
            // that literal when a parked run is known: otherwise it is an ordinary prompt.
            if (parkedRunId is not null
                && string.Equals(line, "continue", StringComparison.OrdinalIgnoreCase))
            {
                if (!cmd.Json)
                {
                    await _out.WriteLineAsync(Tui.InteractiveLineReader.BuildSubmitEcho(line, width: Tui.TuiViewport.Width())).ConfigureAwait(false);
                }
                try
                {
                    await ContinueParkedRunAsync(null).ConfigureAwait(false);
                }
                catch (RunWaitingApprovalException)
                {
                    return 0;
                }
                continue;
            }

            turn++;
            if (landingScreenVisible)
            {
                landingScreenVisible = false;
                if (altBufferActive)
                {
                    // The AltBuffer has no scrollback; 3J can reach through to the normal
                    // buffer's saved lines on some terminals via ConPTY, so only 2J runs.
                    sessionRenderer.Surface.Repaint(writer => writer.Write(FullRebuildClearSequence(altBufferActive)));
                }
                else if (!cmd.Json && _out == Console.Out && !Console.IsOutputRedirected)
                {
                    try { Console.Clear(); _out.Write("\u001b[3J\u001b[H"); } catch (IOException) { /* No live console to clear. */ }
                }
            }

            // Idle output (slash listings, notices) can scroll the sequential rows; the pinned
            // header is restored before the region takes over. No-op in the sequential layout.
            PaintAltHeader();
            var presetUpdate = await _runStartCore.UpdateSessionPresetAsync(
                sessionId,
                cmd.Preset,
                cmd.ConfirmFullAccess,
                cancellationToken).ConfigureAwait(false);
            if (presetUpdate is null)
            {
                return 1;
            }

            var idempotencyKey = Guid.NewGuid().ToString("N");
            var request = new CreateAgentRunRequest(
                sessionId,
                canonicalWorkspace,
                line,
                cmd.Preset,
                presetUpdate.Version,
                idempotencyKey);
            var presetCommitted = false;

            try
            {
                turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Parked state is per-turn: a new prompt starts clean, so the T1 cancel
                // guard can never POST a cancel against a park left over from a prior turn.
                activeRunId = null;
                parkedRunId = null;
                assistantPayloadRefs.Clear();
                sessionRenderer.Width = Tui.TuiViewport.Width();
                sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
                typeAhead.TurnActive = true;
                if (ApplyFooterLayout(repaint: true))
                {
                    await StartTypeAheadAsync().ConfigureAwait(false);
                }

                if (sticky.Active)
                {
                    sticky.RestoreTranscriptCursor();
                }
                if (!cmd.Json)
                {
                    sessionRenderer.Surface.WriteLine(Tui.InteractiveLineReader.BuildSubmitEcho(line, width: sessionRenderer.Width));
                }
                sessionRenderer.BeginTurn();
                var resp = await _agentClient.CreateRunAsync(request, turnCts.Token).ConfigureAwait(false);
                activeRunId = resp.RunId;

                var isTrustWait = string.Equals(
                    resp.Status,
                    "waiting_workspace_trust",
                    StringComparison.OrdinalIgnoreCase);

                // The composer can ask, so ask here rather than parking the run. The old
                // path wrote the instruction into the transcript and left the turn live,
                // where the status row kept spinning "Thinking" and the sticky footer
                // repainted the line away: the operator saw a slow model and a frozen
                // prompt. Nothing is executing while the challenge is open, so answering it
                // in place costs nothing and keeps trust an explicit human decision.
                // --json never reaches the in-composer ask: it requires --trust-workspace,
                // so the human notice stays on stderr through RunStartCore.HandleWorkspaceTrustAsync.
                var trustDeclined = false;
                var trusted = isTrustWait && !cmd.TrustWorkspace
                    ? await ResolveTrust().ConfigureAwait(false)
                    : await _runStartCore.HandleWorkspaceTrustAsync(
                            resp,
                            sessionId,
                            cmd.Preset,
                            cmd.TrustWorkspace,
                            cmd.Json,
                            turnCts.Token)
                        .ConfigureAwait(false);

                async Task<bool> ResolveTrust()
                {
                    var outcome = await ResolveWorkspaceTrustInPlaceAsync(
                            resp,
                            sessionId,
                            cmd.Preset,
                            canonicalWorkspace,
                            cmd.Json,
                            turnCts.Token)
                        .ConfigureAwait(false);
                    trustDeclined = outcome.Declined;
                    return outcome.Trusted;
                }

                if (!trusted && isTrustWait)
                {
                    // Either the operator declined — the run is cancelled — or there was no
                    // composer to ask in and the bounded code was already reported. Both end
                    // this turn: nothing is executing, and the live row must not keep spinning
                    // over a run that is not going to produce a reply.
                    await FinishTurnAsync().ConfigureAwait(false);
                    if (trustDeclined)
                    {
                        sessionRenderer.Note("workspace not trusted · this run was cancelled", Tui.TuiToken.Err);
                    }

                    continue;
                }

                if (!trusted)
                {
                    if (!await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                    {
                        await FinishTurnAsync().ConfigureAwait(false);
                        return 1;
                    }

                    await FinishTurnAsync().ConfigureAwait(false);
                    continue;
                }

                presetCommitted = true;

                await FollowRunAsync(resp.RunId, 0, turnCts.Token).ConfigureAwait(false);
                await FinishTurnAsync().ConfigureAwait(false);
            }
            catch (RunWaitingApprovalException)
            {
                // --json approval escape: the bounded notice already went to stderr, and
                // the run is deliberately left waiting server-side (never auto-denied).
                // Ending the chat hands control back to the scripted consumer, which
                // answers it with `ru agent approve` instead of this process reading a
                // prompt it must not consume.
                await FinishTurnAsync().ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException)
            {
                // The token only stops local streaming; the run must be cancelled server-side
                // too, like one-shot `agent run` and the GUI Stop, or it keeps the workspace
                // lock -- unless the run parked itself, which makes it the user's to resume.
                if (!presetCommitted
                    && !await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    return 1;
                }
                await ReportInterruptionAsync().ConfigureAwait(false);
            }
            catch (AgentApiException ex) when (ex.ErrorCode == "workspace_busy")
            {
                if (!presetCommitted
                    && !await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    return 1;
                }
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteBusyConflictAsync(ex, WriteAltLineAsync).ConfigureAwait(false);
            }
            catch (AgentApiException ex)
            {
                // Pre-run rejections (missing credential, no model) already name
                // themselves: one error line, no supplementary text.
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteAltLineAsync(_err, Tui.TuiPalette.Err(CliErrorText.Format(ex))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!presetCommitted
                    && !await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    return 1;
                }
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteAltLineAsync(_err, Tui.TuiPalette.Err($"run_error: {ex.Message}")).ConfigureAwait(false);
            }
            await _out.FlushAsync().ConfigureAwait(false);
        }
        }
        finally
        {
            await StopTypeAheadAsync().ConfigureAwait(false);
            if (!cmd.Json)
            {
                sticky.Exit();
                sessionRenderer.Surface.Repaint(w => w.Write("\u001b[r"));
            }
        }

        return 0;
    }

    /// <summary>
    /// Writes one diagnostic line so it can never smudge the pinned full-screen frame. In
    /// the AltBuffer layout the hardware cursor can be parked on the composer row after a
    /// footer paint, so a raw stderr write lands on the frame and its trailing line feed on
    /// the last physical row scrolls the whole screen, unpinning the header. Restoring the
    /// transcript cursor first lands the line in the scrolling region, where it scrolls with
    /// the transcript and survives the next footer repaint; the sequential/fallback layouts
    /// write exactly as before.
    /// </summary>
    public static async Task WriteAltSafeAsync(
        Tui.TuiStickyRegion sticky,
        bool altBufferActive,
        TextWriter writer,
        string line)
    {
        ArgumentNullException.ThrowIfNull(sticky);
        ArgumentNullException.ThrowIfNull(writer);
        if (altBufferActive && sticky.Active)
        {
            sticky.RestoreTranscriptCursor(writer);
        }

        await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    /// <summary>
    /// A busy workspace is operator-recoverable: name the run that owns it and how to
    /// release it instead of printing the raw error envelope. Every line goes through
    /// <paramref name="writeLineAsync"/> so the AltBuffer frame is never smudged.
    /// </summary>
    private async Task WriteBusyConflictAsync(
        AgentApiException error,
        Func<TextWriter, string, Task> writeLineAsync)
    {
        ArgumentNullException.ThrowIfNull(writeLineAsync);
        var blockingRunId = error.BlockingRunId;
        if (string.IsNullOrWhiteSpace(blockingRunId))
        {
            await writeLineAsync(_err, Tui.TuiPalette.Err("workspace_busy: another Agent run still owns this workspace.")).ConfigureAwait(false);
            await writeLineAsync(_err, Tui.TuiPalette.DimPaint("  release the blocking run with 'ru agent cancel <run-id>'.")).ConfigureAwait(false);
            await _err.FlushAsync().ConfigureAwait(false);
            return;
        }

        var status = string.IsNullOrWhiteSpace(error.BlockingRunStatus) ? "active" : error.BlockingRunStatus;
        await writeLineAsync(_err, Tui.TuiPalette.Err($"workspace_busy: run {blockingRunId} ({status}) still owns this workspace.")).ConfigureAwait(false);
        var hint = IsParkedRunStatus(status)
            ? $"  answer it with 'ru agent attach {blockingRunId}', continue it with 'ru agent continue {blockingRunId}', or release it with 'ru agent cancel {blockingRunId}'."
            : $"  answer it with 'ru agent attach {blockingRunId}', or release it with 'ru agent cancel {blockingRunId}'.";
        await writeLineAsync(_err, Tui.TuiPalette.DimPaint(hint)).ConfigureAwait(false);
        await _err.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Clear sequence for a full in-place rebuild. The AltBuffer screen has no scrollback of
    /// its own, and on some terminals via ConPTY <c>3J</c> reaches through to the normal
    /// buffer's saved lines; only the sequential/fallback layout clears those.
    /// </summary>
    public static string FullRebuildClearSequence(bool altBufferActive) =>
        altBufferActive ? "\u001b[r\u001b[2J\u001b[H" : "\u001b[r\u001b[3J\u001b[2J\u001b[H";

    private static bool IsTerminalRunStatus(string? status) =>
        status is not null
        && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("interrupted", StringComparison.OrdinalIgnoreCase));

    private static bool IsParkedRunStatus(string? status) =>
        string.Equals(status, "waiting_user", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "reconciliation_required", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when a durable frame transitions the run into a parked state. Keyed on the
    /// envelope Status (the authoritative run state) rather than the event name, because
    /// the same event types also carry the resume and are silent then.
    /// </summary>
    private static bool TryReadParkedTransition(SseFrame frame, out string? runId)
    {
        runId = null;
        if (frame.IsTransient || frame.Data is null)
        {
            return false;
        }

        if (!frame.Data.Contains("\"continue.requested\"", StringComparison.Ordinal)
            && !frame.Data.Contains("\"run.state_changed\"", StringComparison.Ordinal)
            && !frame.Data.Contains("\"reconciliation.required\"", StringComparison.Ordinal))
        {
            return false;
        }

        HarnessSafeEventEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<HarnessSafeEventEnvelope>(frame.Data);
        }
        catch (JsonException)
        {
            return false;
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.RunId))
        {
            return false;
        }

        var parked = envelope.EventType switch
        {
            "continue.requested" => string.Equals(envelope.Status, "waiting_user", StringComparison.OrdinalIgnoreCase),
            "run.state_changed" or "reconciliation.required" => IsParkedRunStatus(envelope.Status),
            _ => false,
        };
        if (!parked)
        {
            return false;
        }

        runId = envelope.RunId;
        return true;
    }

    /// <summary>
    /// Signals that a --json chat turn stopped following a run that is waiting for an
    /// operator approval. The run is left waiting server-side (never auto-approved or
    /// auto-denied); the chat exits so the scripted consumer can answer it with
    /// <c>ru agent approve</c>.
    /// </summary>
    private sealed class RunWaitingApprovalException : Exception
    {
        public RunWaitingApprovalException(string runId, string approvalId)
            : base($"run {runId} is waiting for approval {approvalId}")
        {
            RunId = runId;
            ApprovalId = approvalId;
        }

        public string RunId { get; }

        public string ApprovalId { get; }
    }
}
