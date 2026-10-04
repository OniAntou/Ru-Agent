using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using Terminal.Gui.App;

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

        // Engine selection happens once settings are known: --json, --classic, redirected
        // output, NO_COLOR, a dumb TERM and a too-small viewport all stay sequential. In TG
        // mode the window and its transcript writer are created before the session renderer
        // so the shared run machinery renders into the Terminal.Gui transcript, never stdout.
        var engine = TuiEngineSelection.Resolve(
            Console.IsOutputRedirected,
            cmd.Json,
            Tui.TuiViewport.Width(),
            Tui.TuiViewport.Height(),
            Environment.GetEnvironmentVariable("NO_COLOR"),
            Environment.GetEnvironmentVariable("TERM"),
            cmd.Classic);
        bool tgMode = engine == TuiEngine.TerminalGui;
        using IApplication? tgApp = tgMode ? Application.Create() : null;
        if (tgApp is not null)
        {
            tgApp.Init();
        }

        using TgChatWindow? tgWindow = tgMode ? TgChatWindow.Build() : null;
        TgTranscriptWriter? tgWriter = tgMode ? new TgTranscriptWriter(tgApp!, tgWindow!.Transcript) : null;
        // The loop is created once the run handlers exist (RunTerminalGuiChatAsync);
        // attachment mutations below refresh its hint row through this seam.
        TgChatLoop? tgLoop = null;

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
        // Terminal.Gui owns the screen: human lines land in the transcript view.
        var humanOut = cmd.Json ? _err : (TextWriter?)tgWriter ?? _out;

        var ws = Tui.TuiHeader.ShortenWorkspace(canonicalWorkspace, 48);
        var workspaceLabel = ws;

        var pendingImages = new List<Images.CliImageAttachment>();
        var pendingTexts = new List<Attachments.CliTextAttachment>();
        if (cmd.Images is { Count: > 0 })
        {
            foreach (var image in cmd.Images)
            {
                var (attachment, imageError) = await Images.CliImageFiles
                    .LoadAsync(image, canonicalWorkspace, cancellationToken)
                    .ConfigureAwait(false);
                if (attachment is null)
                {
                    await _err.WriteLineAsync(imageError).ConfigureAwait(false);
                    return 1;
                }

                pendingImages.Add(attachment);
            }
        }

        var sessionRenderer = new Tui.TuiSessionRenderer(tgWriter ?? (TextWriter)_out);
        if (tgWriter is not null)
        {
            // Ctrl+O parity: a classic row that advertises expansion (`· ctrl+o`) carries
            // its hidden diff/thinking rows in the renderer, not on the wire. The provider
            // lets the bridge hand them to the TG transcript so expansion works end to end.
            tgWriter.RetainedLinesProvider = () => sessionRenderer.Transcript.RetainedLines;
        }

        var lineReader = new Tui.InteractiveLineReader
        {
            TopLabel = workspaceLabel,
        };

        if (!cmd.Json && !tgMode && _out == Console.Out && !Console.IsOutputRedirected)
        {
            try { Console.Clear(); _out.Write("\u001b[3J\u001b[H"); } catch (IOException) { /* No live console to clear. */ }
        }

        // Header state for the Terminal.Gui header. The title is read from the
        // session document (bounded probes) and the tally repaints as usage frames arrive.
        string? sessionTitle = null;
        long? lastTotalTokens = null;
        var usage = new Tui.TuiUsageTracker();
        var sessionTitleProbes = 0;

        // Best effort: the header's percent and cost need the catalog's published context
        // window and rates; a catalog that cannot answer leaves both off rather than
        // inventing numbers.
        async Task RefreshModelUsageInfoAsync(CancellationToken ct)
        {
            usage.ContextLimit = null;
            usage.InputRate = null;
            usage.OutputRate = null;
            // The usage cluster only exists on the TG header; a sequential/data-only
            // chat must not pay a catalog read for numbers it never shows.
            if (!tgMode || activeModel.Length == 0)
            {
                return;
            }

            try
            {
                var details = await _settingsClient
                    .GetModelDetailsAsync(backendName is "(provider unknown)" ? null : backendName, activeModel, ct)
                    .ConfigureAwait(false);
                if (details is null)
                {
                    return;
                }

                usage.ContextLimit = details.ContextLength;
                if (details.InputCost is { } inputRate && details.OutputCost is { } outputRate)
                {
                    usage.InputRate = inputRate;
                    usage.OutputRate = outputRate;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Tui.TuiDebug.Log("usage", $"model details read failed: {CliErrorText.ShortMessage(ex.Message)}");
            }
        }

        // Best effort and bounded: a fresh session may not have a title until the host has
        // seen a message, so the header keeps the plain caption until one arrives. The probe
        // only runs where the header exists and never blocks turn cleanup.
        async Task RefreshSessionTitleAsync()
        {
            if (!tgMode || !string.IsNullOrWhiteSpace(sessionTitle) || sessionTitleProbes >= 3)
            {
                return;
            }

            sessionTitleProbes++;
            try
            {
                var doc = await _sessionClient.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (doc.TryGetProperty("session", out var sessionElement)
                    && sessionElement.TryGetProperty("title", out var titleElement)
                    && titleElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(titleElement.GetString()))
                {
                    sessionTitle = titleElement.GetString()!.Trim();
                    RunOnTgUi(() => tgWindow!.Header.SetTitle(sessionTitle));
                }
            }
            catch (OperationCanceledException)
            {
                // The enclosing turn was cancelled: the probe must not swallow the cleanup
                // and the server-side run cancel that follow it.
            }
            catch (Exception ex)
            {
                // The header keeps its caption when the title cannot be read.
                Tui.TuiDebug.Log("header", $"session title read failed: {CliErrorText.ShortMessage(ex.Message)}");
            }
        }

        CancellationTokenSource? turnCts = null;
        string? activeRunId = null;
        string? parkedRunId = null;
        var assistantPayloadRefs = new List<string>();

        // Slash-command surfaces: the pickers run through the TG panels or the sequential
        // reader. --json owns stdout, so every human-facing line a slash command prints goes
        // to stderr and picker-only commands refuse instead of prompting.
        _slashCommands.HumanOut = humanOut;
        _slashCommands.Json = cmd.Json;
        _slashCommands.PanelWidth = Tui.TuiViewport.Width();
        // The TG frame has no scrollback behind the picker, so a cancelled bare form must
        // reprint the option panel instead of leaving the operator with a silent no-op.
        _slashCommands.ReprintPanelOnPickerCancel = tgMode;
        _slashCommands.Picker = (list, ct) => cmd.Json
            ? Task.FromResult<string?>(null)
            : tgMode
                ? TgPanelRequests.RequestChoiceAsync(tgWindow!, list, ct)
                : lineReader.ReadChoiceAsync(list, _out, ct);
        _slashCommands.SecretPrompt = (prompt, ct) => cmd.Json
            ? Task.FromResult<string?>(null)
            : tgMode
                ? TgPanelRequests.RequestSecretAsync(tgWindow!, null, ct)
                : lineReader.ReadSecretAsync(_out, prompt, ct);
        _slashCommands.SecretPanelPrompt = (providerDisplay, ct) => cmd.Json
            ? Task.FromResult<string?>(null)
            : tgMode
                ? TgPanelRequests.RequestSecretAsync(tgWindow!, providerDisplay, ct)
                : lineReader.ReadSecretPanelAsync(_out, providerDisplay, Tui.TuiViewport.Width(), ct);

        int turn = 0;
        bool landingScreenVisible = true;

        // The composer reopens its own frame after a settled resize; this rebuilds the
        // transcript around it so the new width applies end to end.
        lineReader.OnScreenRefreshedAsync = () =>
        {
            RedrawScreenForResize();
            return Task.CompletedTask;
        };
        lineReader.OnPasteImageAsync = ClipboardAttachAsync;

        void RenderLandingScreen()
        {
            if (cmd.Json || cmd.Classic || tgMode || !Tui.TuiViewport.IsLiveConsole)
            {
                return;
            }

            var rows = Tui.TuiViewport.Height();
            var width = Tui.TuiViewport.Width();
            var landingLines = Tui.TuiHeader.ComposeLandingScreen(width, rows);

            foreach (var line in landingLines)
            {
                _out.WriteLine(line);
            }

            // The sequential composer owns the box below the landing content.
            var boxHeight = !string.IsNullOrWhiteSpace(lineReader.Hints)
                ? Tui.ComposerBox.Height + 1
                : Tui.ComposerBox.Height;
            var boxTop = Math.Max(1, rows - boxHeight + 1);
            var cursorRow = landingLines.Count + 1;
            for (var i = cursorRow; i < boxTop; i++)
            {
                _out.WriteLine(new string(' ', width));
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
            if (cmd.Json || tgMode)
            {
                // The NDJSON trace has no screen to clear, and Console.Clear/ANSI
                // bytes would corrupt stdout; Terminal.Gui owns its own redraws.
                return;
            }

            if (_out == Console.Out && !Console.IsOutputRedirected)
            {
                try { Console.Clear(); } catch (IOException) { }
            }
            sessionRenderer.Surface.Repaint(writer => writer.Write("\u001b[r\u001b[3J\u001b[2J\u001b[H"));

            var rows = Tui.TuiViewport.Height();
            var width = Tui.TuiViewport.Width();
            sessionRenderer.Width = width;

            if (landingScreenVisible && turn == 0)
            {
                RenderLandingScreen();
                return;
            }

            // The status row and composer frame reopen below the reprinted tail.
            var footerHeight = 1 + Tui.ComposerBox.Height;
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
            var boxHeight = !string.IsNullOrWhiteSpace(lineReader.Hints)
                ? Tui.ComposerBox.Height + 1
                : Tui.ComposerBox.Height;
            var boxTop = Math.Max(1, rows - boxHeight + 1);
            var cursorRow = tail.Count + 1;
            for (var i = cursorRow; i < boxTop; i++)
            {
                _out.WriteLine();
            }

            _out.Flush();

            Tui.TuiDebug.Log(
                "layout",
                $"redraw rows={rows} width={width} tail={tail.Count} budget={budget} history={sessionRenderer.Surface.HistoryCount}");
        }

        // Every diagnostic line the REPL prints goes through here so --json keeps stdout
        // pure and TG mode lands the line in its transcript; sequential writes go to the
        // requested writer unchanged.
        Task WriteDiagnosticLineAsync(TextWriter writer, string line) =>
            ResolveDiagnosticWriter(tgMode, tgWriter, writer).WriteLineAsync(line);

        static void DeleteTempPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Temp cleanup is best-effort; the file lives under %TEMP%.
            }
        }

        static void DeleteTempFile(Images.CliImageAttachment attachment) => DeleteTempPath(attachment.TempPath);

        string CurrentAttachmentLabel()
        {
            var count = pendingImages.Count + pendingTexts.Count;
            return count == 0
                ? workspaceLabel
                : $"[{count} attachment{(count == 1 ? string.Empty : "s")}] {workspaceLabel}";
        }

        void RefreshAttachmentLabels()
        {
            var label = CurrentAttachmentLabel();
            lineReader.TopLabel = label;
            // The TG composer does not read TopLabel: push the same count/workspace
            // wording into its hint row after every mutation.
            if (tgMode)
            {
                tgLoop?.RefreshAttachmentLabel();
            }
        }

        /// <summary>
        /// Runs one TG view mutation on the Terminal.Gui loop. The frame/probe paths resume
        /// on thread-pool continuations, so any direct view write from here would race the
        /// driver's draw; an already-disposed session drops the late mutation instead.
        /// </summary>
        void RunOnTgUi(Action action)
        {
            try
            {
                tgApp!.Invoke(action);
            }
            catch (NotInitializedException)
            {
                // The TG session wound down; late view mutations are dropped.
            }
        }

        async Task AttachImageAsync(string path, string? tempPath = null)
        {
            if (pendingImages.Count >= Images.CliImagePolicy.MaxImageCount)
            {
                DeleteTempPath(tempPath);
                await WriteDiagnosticLineAsync(humanOut, Images.CliImagePolicy.TooManyImagesStatus).ConfigureAwait(false);
                return;
            }

            var (attachment, imageError) = await Images.CliImageFiles
                .LoadAsync(path, canonicalWorkspace, cancellationToken)
                .ConfigureAwait(false);
            if (attachment is null)
            {
                DeleteTempPath(tempPath);
                await WriteDiagnosticLineAsync(_err, imageError ?? "invalid_image: the attachment could not be loaded").ConfigureAwait(false);
                return;
            }

            var totalBytes = pendingImages.Sum(image => image.ByteSize) + attachment.ByteSize;
            if (totalBytes > Images.CliImagePolicy.MaxTotalImageBytes)
            {
                DeleteTempPath(tempPath);
                await WriteDiagnosticLineAsync(
                    _err,
                    $"image_too_large: this turn's images total {Images.CliImageAttachment.FormatBytes(totalBytes)} (limit {Images.CliImageAttachment.FormatBytes(Images.CliImagePolicy.MaxTotalImageBytes)}); remove some with /detach").ConfigureAwait(false);
                return;
            }

            if (tempPath is not null)
            {
                attachment = attachment with { TempPath = tempPath };
            }

            pendingImages.Add(attachment);
            RefreshAttachmentLabels();
            if (activeModel.Length > 0 && !Images.CliVisionModels.IsVisionModel(activeModel))
            {
                await WriteDiagnosticLineAsync(
                    _err,
                    $"model_vision_unsupported: {activeModel} may not read images; switch to a vision model with /model").ConfigureAwait(false);
            }

            await WriteDiagnosticLineAsync(humanOut, $"attached [{pendingImages.Count + pendingTexts.Count}] {attachment.Label}").ConfigureAwait(false);
        }

        async Task AttachFileAsync(string path)
        {
            if (Images.CliImagePolicy.IsImageFile(path))
            {
                await AttachImageAsync(path).ConfigureAwait(false);
                return;
            }

            var (attachment, textError) = await Attachments.CliTextFiles
                .LoadAsync(path, canonicalWorkspace, cancellationToken)
                .ConfigureAwait(false);
            if (attachment is null)
            {
                await WriteDiagnosticLineAsync(_err, textError ?? "unsupported_attachment: the file could not be loaded").ConfigureAwait(false);
                return;
            }

            pendingTexts.Add(attachment);
            RefreshAttachmentLabels();
            await WriteDiagnosticLineAsync(humanOut, $"attached [{pendingImages.Count + pendingTexts.Count}] {attachment.Label}").ConfigureAwait(false);
        }

        /// <summary>
        /// Appends pasted text to the TG composer on the UI loop; the composer itself never
        /// sees the Ctrl+V (the clipboard read runs here), so the two cannot double-paste.
        /// </summary>
        void InsertIntoComposer(string text)
        {
            try
            {
                tgApp!.Invoke(() =>
                {
                    var input = tgWindow!.Composer.Input;
                    var current = input.Text?.ToString() ?? string.Empty;
                    var room = 4096 - current.Length;
                    if (room <= 0)
                    {
                        return;
                    }

                    input.Text = current + (text.Length > room ? text[..room] : text);
                });
            }
            catch (NotInitializedException)
            {
                // The TG session wound down before the paste landed.
            }
        }

        async Task ClipboardAttachAsync()
        {
            var clipboard = Images.CliClipboard.TryReadImagePath();
            if (clipboard.Path is null)
            {
                // A text-only clipboard still pastes: Ctrl+V would otherwise swallow the
                // keystroke with only an error line.
                if (tgMode)
                {
                    var text = Images.CliClipboard.TryReadText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var flat = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
                        if (flat.Length > 0)
                        {
                            InsertIntoComposer(flat);
                            return;
                        }
                    }
                }

                await WriteDiagnosticLineAsync(_err, clipboard.Error ?? "clipboard_has_no_image: copy a screenshot or an image file first").ConfigureAwait(false);
                return;
            }

            var name = Path.GetFileName(clipboard.Path);
            var isTemp = name.StartsWith("ru-img-", StringComparison.Ordinal)
                && string.Equals(Path.GetDirectoryName(clipboard.Path), Path.GetTempPath().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            await AttachImageAsync(clipboard.Path, isTemp ? clipboard.Path : null).ConfigureAwait(false);
        }

        bool IsDroppedAttachmentPath(string candidate)
        {
            if (candidate.Length > AgentCliParser.MaxFilePathLength)
            {
                return false;
            }

            // A bare file name ("README.md") stays a prompt: only a rooted path or one
            // with a separator reads as a drop, so ordinary prose cannot attach by accident.
            if (!Path.IsPathRooted(candidate)
                && !candidate.Contains('/')
                && !candidate.Contains('\\'))
            {
                return false;
            }

            if (!Images.CliImagePolicy.IsImageFile(candidate)
                && !Images.CliImagePolicy.IsTextOrCodeFile(candidate)
                && !Attachments.OfficeDocumentText.IsSupported(candidate))
            {
                return false;
            }

            try
            {
                return File.Exists(Images.CliImageFiles.ResolvePath(candidate, canonicalWorkspace));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// Answers one approval. The sequential layout keeps the numbered stdin prompt;
        /// cancelling the run releases the wait instead of leaving the caller blocked.
        /// </summary>
        async Task<string> RequestApprovalAsync(Tui.ApprovalPrompt prompt, CancellationToken approvalToken)
        {
            if (tgMode)
            {
                return await RequestApprovalInTgAsync(prompt, approvalToken).ConfigureAwait(false);
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
        /// The TG approval: the panel floats over the transcript and the window's loop owns
        /// the keyboard. Escape denies — which ends the tool call, not the session — exactly
        /// like the pinned-footer path, so the turn keeps streaming its settled rows.
        /// </summary>
        Task<string> RequestApprovalInTgAsync(Tui.ApprovalPrompt prompt, CancellationToken approvalToken) =>
            RunTgPanelAsync(() => tgWindow!.ShowApproval(prompt), approvalToken);

        /// <summary>The TG workspace-trust ask; Escape is a cancel, never a silent grant.</summary>
        Task<string> RequestTrustInTgAsync(Tui.ApprovalPrompt prompt, CancellationToken trustToken) =>
            RunTgPanelAsync(() => tgWindow!.ShowWorkspaceTrust(prompt), trustToken);

        /// <summary>
        /// Runs one floating panel on the Terminal.Gui loop and awaits the answer. The turn
        /// streams on a background task, so the panel is shown through the app's dispatcher;
        /// the token releases the wait when the turn is cancelled underneath it. The panel
        /// always closes in the finally, and a wind-down with the app already stopped is a
        /// no-op instead of an error.
        /// </summary>
        async Task<string> RunTgPanelAsync(Action show, CancellationToken panelToken)
        {
            var decision = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnConfirmed(string value) => decision.TrySetResult(value);
            void OnCancelled() => decision.TrySetResult(Tui.ApprovalPrompt.Deny);

            tgApp!.Invoke(() =>
            {
                tgWindow!.PanelConfirmed += OnConfirmed;
                tgWindow.PanelCancelled += OnCancelled;
                show();
            });

            using var registration = panelToken.Register(() => decision.TrySetCanceled(panelToken));
            try
            {
                return await decision.Task.ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    tgApp.Invoke(() =>
                    {
                        tgWindow!.PanelConfirmed -= OnConfirmed;
                        tgWindow.PanelCancelled -= OnCancelled;
                        tgWindow.HidePanel();
                    });
                }
                catch (NotInitializedException)
                {
                    // The TG session wound down while the panel was open; there is no view
                    // left to close and the enclosing turn is ending anyway.
                }
            }
        }

        /// <summary>
        /// Asks the operator to trust the workspace while the run holds the challenge open.
        /// Only the TG frame has a composer to ask in; the sequential fallback returns null
        /// so its caller reports the bounded code. Returning without a decision is a cancel,
        /// never a silent grant.
        /// </summary>
        async Task<string?> PromptWorkspaceTrustAsync(
            Tui.ApprovalPrompt prompt,
            CancellationToken trustToken)
        {
            if (tgMode)
            {
                return await RequestTrustInTgAsync(prompt, trustToken).ConfigureAwait(false);
            }

            return null;
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

                await WriteDiagnosticLineAsync(_err, "workspace_trust_error: trust response does not match the challenge.").ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentApiException ex)
            {
                await WriteDiagnosticLineAsync(_err, $"{ex.ErrorCode}: {ex.Message}").ConfigureAwait(false);
                return false;
            }
            catch (Exception ex)
            {
                await WriteDiagnosticLineAsync(_err, $"workspace_trust_error: {ex.Message}").ConfigureAwait(false);
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
                await WriteDiagnosticLineAsync(_err, $"run_cancel_failed: {ex.Message}").ConfigureAwait(false);
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
                await WriteDiagnosticLineAsync(_err, "agent_create_failed: invalid create response binding.").ConfigureAwait(false);
                return (false, false);
            }

            var trustRequestId = response.TrustRequestId;
            var displayHash = response.DisplayIdentityHash;
            if (string.IsNullOrWhiteSpace(trustRequestId) || string.IsNullOrWhiteSpace(displayHash))
            {
                await WriteDiagnosticLineAsync(_err, "workspace_trust_error: trust challenge is incomplete.").ConfigureAwait(false);
                return (false, false);
            }

            var decision = await PromptWorkspaceTrustAsync(
                    Tui.ApprovalPrompt.ForWorkspaceTrust(workspacePath),
                    trustToken)
                .ConfigureAwait(false);

            if (decision is null)
            {
                // No composer to ask in, so there is no frame to protect and the bounded
                // code keeps its established stream. Never read stdin here: a scripted
                // --json consumer owns it. --json owns stdout, and TG owns the screen, so
                // the notice follows humanOut instead of the raw console writer.
                await (json ? _err : humanOut).WriteLineAsync(
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
                await WriteDiagnosticLineAsync(_err, $"assistant_text_recovery_failed: {ex.Message}").ConfigureAwait(false);
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
                // from the surface history, which is the path a resize already uses. The TG
                // transcript is append-only through the bridge and has no scrollback rebuild,
                // so it takes the bounded notice below instead of pretending a repair.
                if (!tgMode && sessionRenderer.Transcript.RepaintAssistantText(full))
                {
                    RedrawScreenForResize();
                    Tui.TuiDebug.Log("recover", $"repainted turn from durable text ({full.Length} chars)");
                    return;
                }

                await WriteDiagnosticLineAsync(
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
            await RefreshSessionTitleAsync().ConfigureAwait(false);
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
                var backendChanged = !string.IsNullOrWhiteSpace(bName)
                    && !string.Equals(bName, backendName, StringComparison.Ordinal);
                var modelChanged = !string.IsNullOrWhiteSpace(curModel)
                    && !string.Equals(curModel, activeModel, StringComparison.Ordinal);
                if (backendChanged)
                {
                    backendName = bName;
                }

                if (modelChanged)
                {
                    // The composer meta row, the vision pre-check and the usage cluster read
                    // this local, so a /model switch is reflected without a restart.
                    activeModel = curModel;
                }

                if (backendChanged || modelChanged)
                {
                    await RefreshModelUsageInfoAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Live refresh failed; legend keeps the last known values.
                string mLbl = string.IsNullOrWhiteSpace(activeModel) ? "model" : activeModel;
                lineReader.Legend = string.IsNullOrWhiteSpace(reasoningEffort) ? mLbl : $"{mLbl} · {reasoningEffort}";
            }

            if (tgMode)
            {
                // A /model or /backend switch must land on the TG composer meta row too.
                tgWindow!.Composer.SetMeta(cmd.Preset, activeModel, backendName);
            }
        }

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
                await WriteDiagnosticLineAsync(_err, modelWarning).ConfigureAwait(false);
            }
            else
            {
                sessionRenderer.Note(modelWarning, Tui.TuiToken.Warn);
                sessionRenderer.Flush();
            }
        }

        if (pendingImages.Count > 0
            && activeModel.Length > 0
            && !Images.CliVisionModels.IsVisionModel(activeModel))
        {
            var visionWarning =
                $"model_vision_unsupported: {activeModel} may not read images; switch to a vision model with /model";
            if (cmd.Json)
            {
                await WriteDiagnosticLineAsync(_err, visionWarning).ConfigureAwait(false);
            }
            else
            {
                sessionRenderer.Note(visionWarning, Tui.TuiToken.Warn);
                sessionRenderer.Flush();
            }
        }

        await RefreshModelUsageInfoAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(cmd.SessionId))
        {
            // Only an explicit --session reopens someone else's session; a fresh session
            // has no run to adopt and must not spend the round trip.
            await AdoptSessionRunAsync(sessionId).ConfigureAwait(false);
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
                            await WriteDiagnosticLineAsync(
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
                    sessionRenderer.RenderFrame(frame);

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
                        catch (RunWaitingApprovalException)
                        {
                            // The TG escape above: the notice is already in the transcript and
                            // the run is deliberately left waiting server-side.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            await WriteDiagnosticLineAsync(_err, $"inline_approval_error: {ex.Message}").ConfigureAwait(false);
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
                            if (usageEnvelope is not null
                                && (usageEnvelope.TotalTokens is not null
                                    || usageEnvelope.InputTokens is not null
                                    || usageEnvelope.OutputTokens is not null))
                            {
                                usage.Apply(usageEnvelope.InputTokens, usageEnvelope.OutputTokens);
                                lastTotalTokens = usageEnvelope.TotalTokens
                                    ?? ((usageEnvelope.InputTokens ?? 0) + (usageEnvelope.OutputTokens ?? 0));
                                sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
                                if (tgMode)
                                {
                                    RunOnTgUi(() => tgWindow!.Header.UpdateUsage(
                                        lastTotalTokens ?? 0,
                                        usage.ContextPercent,
                                        usage.CostKnown ? (decimal)usage.SessionCost : null));
                                }
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
            // The recovery reprints a lost suffix through the same surface/writer the turn
            // streamed through, so it works for both engines; only the truncation repaint is
            // sequential-only (the TG transcript cannot rebuild what it already painted).
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
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(CliErrorText.Format(ex))).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                // A transport failure must not escape the REPL: report it bounded and keep
                // the prompt (and the parked tracking) intact.
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(
                    $"continue_error: {CliErrorText.ShortMessage(ex.Message)}")).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            if (run.TerminalAt is not null || IsTerminalRunStatus(run.Status))
            {
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(
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
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(
                    $"run_not_parked: run {targetRunId} is {run.Status} and not waiting to continue.")).ConfigureAwait(false);
                await _out.FlushAsync().ConfigureAwait(false);
                return;
            }

            turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sessionRenderer.Width = Tui.TuiViewport.Width();
            sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
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
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(CliErrorText.Format(ex))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err($"continue_error: {ex.Message}")).ConfigureAwait(false);
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
                && sessionElement.ValueKind == JsonValueKind.Object)
            {
                if (sessionElement.TryGetProperty("title", out var titleElement)
                    && titleElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(titleElement.GetString()))
                {
                    sessionTitle = titleElement.GetString()!.Trim();
                }

                if (sessionElement.TryGetProperty("messages", out var messagesElement)
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
                        await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Warn(notice)).ConfigureAwait(false);
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
                        await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.DimPaint(notice)).ConfigureAwait(false);
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

        // The non-slash branch of the sequential key loop, moved into the shape the
        // dispatcher expects. 'continue' became 'return' (both resume the key loop),
        // and dispatcherExitCode carries the paths that used to return from ChatAsync.
        int? dispatcherExitCode = null;

        async Task HandlePromptLineAsync(string promptLine, CancellationToken ct)
        {
            // The GUI accepts a bare typed "Continue" while a run is parked. Only claim
            // that literal when a parked run is known: otherwise it is an ordinary prompt.
            if (parkedRunId is not null
                && string.Equals(promptLine, "continue", StringComparison.OrdinalIgnoreCase))
            {
                if (!cmd.Json && !tgMode)
                {
                    await _out.WriteLineAsync(Tui.InteractiveLineReader.BuildSubmitEcho(promptLine, width: Tui.TuiViewport.Width())).ConfigureAwait(false);
                }
                try
                {
                    await ContinueParkedRunAsync(null).ConfigureAwait(false);
                }
                catch (RunWaitingApprovalException)
                {
                    dispatcherExitCode = 0;
                    return;
                }
                return;
            }

            // The usage envelope's input/output counters restart with every run, so the
            // step-delta baseline resets here; the session cost itself keeps accumulating.
            usage.BeginTurn();
            turn++;
            if (landingScreenVisible)
            {
                landingScreenVisible = false;
                if (!cmd.Json && !tgMode && _out == Console.Out && !Console.IsOutputRedirected)
                {
                    try { Console.Clear(); _out.Write("\u001b[3J\u001b[H"); } catch (IOException) { /* No live console to clear. */ }
                }
            }

            var presetUpdate = await _runStartCore.UpdateSessionPresetAsync(
                sessionId,
                cmd.Preset,
                cmd.ConfirmFullAccess,
                ct).ConfigureAwait(false);
            if (presetUpdate is null)
            {
                dispatcherExitCode = 1;
                return;
            }

            var idempotencyKey = Guid.NewGuid().ToString("N");
            var promptText = Attachments.AttachedPrompt.Compose(promptLine, pendingTexts);
            var imagePayloads = pendingImages.Count == 0
                ? null
                : pendingImages.Select(image => image.ToDataUrl()).ToList();
            var attachmentLabels = new List<string>();
            for (var i = 0; i < pendingImages.Count; i++)
            {
                attachmentLabels.Add($"[image {i + 1}: {pendingImages[i].FileName}]");
            }

            for (var i = 0; i < pendingTexts.Count; i++)
            {
                attachmentLabels.Add($"[file {i + 1}: {pendingTexts[i].FileName}]");
            }

            var request = new CreateAgentRunRequest(
                sessionId,
                canonicalWorkspace,
                promptText,
                cmd.Preset,
                presetUpdate.Version,
                idempotencyKey,
                imagePayloads);
            const int MaxComposedPromptChars = 16_000;
            if (promptText.Length > MaxComposedPromptChars)
            {
                await WriteDiagnosticLineAsync(
                    _err,
                    $"prompt_too_large: the composed prompt is {promptText.Length} chars (limit {MaxComposedPromptChars}); detach or shorten attachments").ConfigureAwait(false);
                return;
            }
            var presetCommitted = false;
            var sentImages = new List<Images.CliImageAttachment>();
            var sentTexts = new List<Attachments.CliTextAttachment>();

            try
            {
                turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // Parked state is per-turn: a new prompt starts clean, so the T1 cancel
                // guard can never POST a cancel against a park left over from a prior turn.
                activeRunId = null;
                parkedRunId = null;
                assistantPayloadRefs.Clear();
                sessionRenderer.Width = Tui.TuiViewport.Width();
                sessionRenderer.Status.SetCounters(lastTotalTokens, 0);
                if (!cmd.Json && !tgMode)
                {
                    sessionRenderer.Surface.WriteLine(Tui.InteractiveLineReader.BuildSubmitEcho(
                        promptLine,
                        width: sessionRenderer.Width,
                        attachmentLabels: attachmentLabels.Count == 0 ? null : attachmentLabels));
                }
                sessionRenderer.BeginTurn();
                var resp = await _agentClient.CreateRunAsync(request, turnCts.Token).ConfigureAwait(false);
                activeRunId = resp.RunId;

                if (pendingImages.Count > 0 || pendingTexts.Count > 0)
                {
                    // The run owns the attachments now. The lists clear immediately, but
                    // the temp files wait for the trust gate: a declined challenge restores
                    // the lists intact, so the files must still exist then.
                    sentImages = pendingImages.ToList();
                    sentTexts = pendingTexts.ToList();
                    pendingImages.Clear();
                    pendingTexts.Clear();
                    RefreshAttachmentLabels();
                }

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
                    // over a run that is not going to produce a reply. The attachments were
                    // never processed, so they come back for a retry.
                    if (sentImages.Count > 0 || sentTexts.Count > 0)
                    {
                        pendingImages.AddRange(sentImages);
                        pendingTexts.AddRange(sentTexts);
                        RefreshAttachmentLabels();
                    }

                    await FinishTurnAsync().ConfigureAwait(false);
                    if (trustDeclined)
                    {
                        sessionRenderer.Note("workspace not trusted · this run was cancelled", Tui.TuiToken.Err);
                    }

                    return;
                }

                if (!trusted)
                {
                    if (!await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                    {
                        await FinishTurnAsync().ConfigureAwait(false);
                        dispatcherExitCode = 1;
                        return;
                    }

                    await FinishTurnAsync().ConfigureAwait(false);
                    return;
                }

                presetCommitted = true;
                foreach (var image in sentImages)
                {
                    DeleteTempFile(image);
                }

                sentImages.Clear();
                sentTexts.Clear();

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
                dispatcherExitCode = 0;
                return;
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
                    dispatcherExitCode = 1;
                    return;
                }
                await ReportInterruptionAsync().ConfigureAwait(false);
            }
            catch (AgentApiException ex) when (ex.ErrorCode == "workspace_busy")
            {
                if (!presetCommitted
                    && !await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    dispatcherExitCode = 1;
                    return;
                }
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteBusyConflictAsync(ex, WriteDiagnosticLineAsync).ConfigureAwait(false);
            }
            catch (AgentApiException ex)
            {
                // Pre-run rejections (missing credential, no model) already name
                // themselves: one error line, no supplementary text.
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err(CliErrorText.Format(ex))).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (!presetCommitted
                    && !await _runStartCore.RestorePresetOrReportAsync(presetUpdate).ConfigureAwait(false))
                {
                    await FinishTurnAsync().ConfigureAwait(false);
                    dispatcherExitCode = 1;
                    return;
                }
                await FinishTurnAsync().ConfigureAwait(false);
                await WriteDiagnosticLineAsync(_err, Tui.TuiPalette.Err($"run_error: {ex.Message}")).ConfigureAwait(false);
            }
            await _out.FlushAsync().ConfigureAwait(false);
        }
        try
        {
            while (!cancellationToken.IsCancellationRequested)
        {
            if (engine == TuiEngine.TerminalGui)
            {
                // Terminal.Gui owns the keyboard and the frame from here on. The sequential
                // loop never reads a line while TG runs: the same slash/prompt handlers below
                // are driven by TgChatLoop, so command behaviour cannot drift between engines.
                await RunTerminalGuiChatAsync().ConfigureAwait(false);
                if (tgWindow!.HandOverToSequential && dispatcherExitCode is null)
                {
                    // The terminal shrank below the 80x24 floor while TG owned the screen,
                    // so the window stopped the app. One-way handover: the same session,
                    // handlers and copy continue on the sequential path. The writers the
                    // shared machinery captured must leave the stopped Terminal.Gui buffer
                    // and point back at the real console.
                    engine = TuiEngine.Sequential;
                    tgMode = false;
                    // The bridge points into the stopped Terminal.Gui app: every diagnostic
                    // written through it after this line would vanish. Diagnostics fall back
                    // to the real console writer for the rest of the session.
                    tgWriter = null;
                    _slashCommands.ReprintPanelOnPickerCancel = false;
                    humanOut = _out;
                    _slashCommands.HumanOut = _out;
                    sessionRenderer = new Tui.TuiSessionRenderer(_out);

                    // Terminal.Gui renders on the alternate screen buffer and only restores
                    // the normal one on dispose; the sequential session must be visible, so
                    // wind the TG application down before the first sequential write. The
                    // local stays set: an in-flight TG continuation may still call Invoke and
                    // must observe NotInitializedException (caught) rather than a null
                    // reference.
                    tgApp!.Dispose();
                    continue;
                }

                return dispatcherExitCode ?? 0;
            }

            // The sequential reader owns idle input.
            var line = await lineReader.ReadLineWithContinuationAsync(_in, humanOut, prompt, contPrompt, cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            line = line.Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(line) && pendingImages.Count == 0 && pendingTexts.Count == 0)
            {
                continue;
            }

            // A quoted path dropped onto the terminal arrives as the whole line; a file
            // that exists on disk attaches instead of being sent as prose.
            if (!string.IsNullOrEmpty(line) && IsDroppedAttachmentPath(line))
            {
                await AttachFileAsync(line).ConfigureAwait(false);
                continue;
            }

            // The slash branch of the sequential key loop, in the shape the dispatcher
            // expects: 'continue' became 'return true' (handled) and the /exit and
            // /continue approval paths record their exit code for the caller below.
            async Task<bool> HandleSlashLineAsync(string slashLine)
            {
                if (landingScreenVisible && !slashLine.StartsWith("/clear", StringComparison.OrdinalIgnoreCase))
                {
                    landingScreenVisible = false;
                }
                if (!cmd.Json && !tgMode)
                {
                    await _out.WriteLineAsync(Tui.InteractiveLineReader.BuildSubmitEcho(slashLine, width: Tui.TuiViewport.Width())).ConfigureAwait(false);
                }
                var parts = slashLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var slashCmd = parts[0].ToLowerInvariant();
                var arg = parts.Length > 1 ? parts[1].Trim() : null;

                if (slashCmd is "/exit" or "/quit")
                {
                    await humanOut.WriteLineAsync(Tui.TuiPalette.DimPaint("Exiting interactive terminal. Goodbye!")).ConfigureAwait(false);
                    await humanOut.FlushAsync().ConfigureAwait(false);
                    dispatcherExitCode = 0;
                    return true;
                }
                if (slashCmd is "/history")
                {
                    // The TG loop owns its own Up/Down history; the sequential reader never
                    // sees a TG line, so reading it there would always report an empty list.
                    var history = tgMode ? tgLoop?.History ?? [] : lineReader.History;
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
                    return true;
                }
                if (slashCmd is "/clear")
                {
                    turn = 0;
                    landingScreenVisible = true;
                    lastTotalTokens = null;
                    usage.BeginTurn();
                    if (tgMode)
                    {
                        // Clear the TG transcript and its header usage cluster in place; the
                        // sequential layout below keeps its existing reset.
                        RunOnTgUi(() => tgWindow!.ResetTranscript());
                    }
                    else if (!cmd.Json && !tgMode && _out == Console.Out && !Console.IsOutputRedirected)
                    {
                        try { Console.Clear(); _out.Write("\u001b[3J\u001b[H"); } catch (IOException) { /* No live console to clear. */ }
                    }
                    RenderLandingScreen();
                    await humanOut.FlushAsync().ConfigureAwait(false);
                    return true;
                }
                if (await _slashCommands.TryHandleAsync(slashCmd, arg, sessionId, cmd.Preset, canonicalWorkspace, cancellationToken).ConfigureAwait(false))
                {
                    await UpdateLegendAsync().ConfigureAwait(false);
                    return true;
                }
                if (slashCmd is "/rewind")
                {
                    if (arg is null || !int.TryParse(arg, out var toIndex))
                    {
                        await WriteDiagnosticLineAsync(_err, "usage: /rewind <message_index>").ConfigureAwait(false);
                        return true;
                    }
                    try
                    {
                        await _sessionClient.RewindAsync(sessionId, toIndex, cancellationToken).ConfigureAwait(false);
                        await humanOut.WriteLineAsync(Tui.TuiPalette.Ok($"✓ Rewound session {sessionId} to message index {toIndex}")).ConfigureAwait(false);
                    }
                    catch (AgentApiException ex)
                    {
                        await WriteDiagnosticLineAsync(_err, $"{CliErrorText.Format(ex)}").ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await WriteDiagnosticLineAsync(_err, $"rewind_error: {ex.Message}").ConfigureAwait(false);
                    }
                    await _out.FlushAsync().ConfigureAwait(false);
                    return true;
                }
                if (slashCmd is "/attach")
                {
                    if (string.IsNullOrWhiteSpace(arg))
                    {
                        await WriteDiagnosticLineAsync(_err, "usage: /attach <path>").ConfigureAwait(false);
                        return true;
                    }

                    await AttachFileAsync(arg).ConfigureAwait(false);
                    return true;
                }
                if (slashCmd is "/attachments")
                {
                    if (pendingImages.Count == 0 && pendingTexts.Count == 0)
                    {
                        await WriteDiagnosticLineAsync(humanOut, "no attachments").ConfigureAwait(false);
                        return true;
                    }

                    for (var i = 0; i < pendingImages.Count; i++)
                    {
                        await WriteDiagnosticLineAsync(humanOut, $"  [{i + 1}] image  {pendingImages[i].Label}").ConfigureAwait(false);
                    }

                    for (var i = 0; i < pendingTexts.Count; i++)
                    {
                        await WriteDiagnosticLineAsync(
                            humanOut,
                            $"  [{pendingImages.Count + i + 1}] file   {pendingTexts[i].Label}").ConfigureAwait(false);
                    }

                    return true;
                }
                if (slashCmd is "/detach")
                {
                    var total = pendingImages.Count + pendingTexts.Count;
                    if (string.Equals(arg, "--all", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var attachment in pendingImages)
                        {
                            DeleteTempFile(attachment);
                        }

                        pendingImages.Clear();
                        pendingTexts.Clear();
                        RefreshAttachmentLabels();
                        await WriteDiagnosticLineAsync(humanOut, $"detached all {total} attachment(s)").ConfigureAwait(false);
                        return true;
                    }

                    if (arg is null || !int.TryParse(arg, out var index) || index < 1 || index > total)
                    {
                        await WriteDiagnosticLineAsync(_err, "usage: /detach <n> | /detach --all").ConfigureAwait(false);
                        return true;
                    }

                    if (index <= pendingImages.Count)
                    {
                        var removedImage = pendingImages[index - 1];
                        pendingImages.RemoveAt(index - 1);
                        DeleteTempFile(removedImage);
                        await WriteDiagnosticLineAsync(humanOut, $"detached {removedImage.FileName}").ConfigureAwait(false);
                    }
                    else
                    {
                        var textIndex = index - pendingImages.Count - 1;
                        var removedText = pendingTexts[textIndex];
                        pendingTexts.RemoveAt(textIndex);
                        await WriteDiagnosticLineAsync(humanOut, $"detached {removedText.FileName}").ConfigureAwait(false);
                    }

                    RefreshAttachmentLabels();
                    return true;
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
                        dispatcherExitCode = 0;
                        return true;
                    }
                    return true;
                }
                await WriteDiagnosticLineAsync(_err, $"unknown_command: {slashCmd}. Type /help for assistance.").ConfigureAwait(false);
                return true;
            }

            /// <summary>
            /// The TG engine's entry point: the window is already built and no sequential
            /// surface fights Terminal.Gui. Slash lines and prompts go through the very same
            /// local handlers the sequential loop calls, with their writers redirected to
            /// the transcript.
            /// </summary>
            async Task RunTerminalGuiChatAsync()
            {
                var loop = new TgChatLoop(tgWindow!, cancellationToken);
                tgLoop = loop;
                var guiDispatcher = new ChatLineDispatcher(HandleSlashLineAsync, HandlePromptLineAsync);
                loop.SetPromptHandler(async (line, _, ct) =>
                {
                    await guiDispatcher.DispatchAsync(line, ct).ConfigureAwait(false);
                    if (dispatcherExitCode is not null)
                    {
                        // /exit and the fatal prompt paths record an exit code; the TG frame
                        // has no natural end, so stop both the loop and the app.
                        loop.Stop();
                        tgApp!.Invoke(() => tgApp.RequestStop());
                    }
                });

                // Attachment mutations refresh the hint row through RefreshAttachmentLabels;
                // these seams let the loop ask for the label/count and claim Ctrl+V
                // clipboard pastes and dropped paths without parsing anything itself.
                loop.SetAttachmentLabelProvider(CurrentAttachmentLabel);
                loop.SetAttachmentCountProvider(() => pendingImages.Count + pendingTexts.Count);
                loop.SetPasteHandler(ClipboardAttachAsync);
                loop.SetDropHandler(line =>
                {
                    // Same shape as the classic idle loop: a quoted/rooted path that exists
                    // on disk attaches instead of being sent as prose.
                    var candidate = line.Trim().Trim('"', '\'');
                    if (string.IsNullOrEmpty(candidate) || !IsDroppedAttachmentPath(candidate))
                    {
                        return false;
                    }

                    _ = RunAttachmentAsync(() => AttachFileAsync(candidate));
                    return true;
                });

                // The drop attach starts its own task from a synchronous handler; failures
                // that slip past AttachFileAsync's own error rows land in the transcript.
                async Task RunAttachmentAsync(Func<Task> action)
                {
                    try
                    {
                        await action().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await WriteDiagnosticLineAsync(_err, $"attachment_error: {CliErrorText.ShortMessage(ex.Message)}").ConfigureAwait(false);
                    }
                }

                tgWindow!.Composer.SetMeta(cmd.Preset, activeModel, backendName);
                loop.RefreshAttachmentLabel();
                tgApp!.AddTimeout(
                    TimeSpan.FromMilliseconds(200),
                    () =>
                    {
                        tgWindow.FocusComposer();
                        return false;
                    });
                tgApp.AddTimeout(
                    TimeSpan.FromMilliseconds(TuiHeader.MeteorFrameMilliseconds),
                    () =>
                    {
                        tgWindow.Landing.Tick();
                        return true;
                    });
                tgApp.Run(tgWindow);
                // A stop that did not come from /exit (window closed under us) must still
                // release the completion the caller awaits.
                if (tgWindow.HandOverToSequential)
                {
                    // The tiny-terminal stop ends the TG frame for good: an in-flight turn
                    // must not keep streaming into a stopped app (a floating panel can no
                    // longer be answered), and Stop() keeps the queue from firing later.
                    loop.Cancel();
                }

                loop.Stop();
                await loop.Completion.ConfigureAwait(false);
            }

            var dispatcher = new ChatLineDispatcher(HandleSlashLineAsync, HandlePromptLineAsync);
            await dispatcher.DispatchAsync(line, cancellationToken).ConfigureAwait(false);
            if (dispatcherExitCode is int exitCode)
            {
                return exitCode;
            }
        }
        }
        finally
        {
            // Pending clipboard screenshots do not outlive the chat that owns them.
            foreach (var image in pendingImages)
            {
                DeleteTempFile(image);
            }
        }

        return 0;
    }

    /// <summary>
    /// Routes one submitted line to the slash-command handler or the prompt runner. The
    /// sequential and Terminal.Gui loops both call this so command behaviour
    /// (attachments, /model, /attach, trust prompts) cannot drift between engines.
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
    public static TextWriter ResolveDiagnosticWriter(bool tgMode, TextWriter? tgWriter, TextWriter fallback) =>
        tgMode && tgWriter is not null ? tgWriter : fallback;

    /// <summary>
    /// A busy workspace is operator-recoverable: name the run that owns it and how to
    /// release it instead of printing the raw error envelope. Every line goes through
    /// <paramref name="writeLineAsync"/> so it lands on the resolved writer.
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
