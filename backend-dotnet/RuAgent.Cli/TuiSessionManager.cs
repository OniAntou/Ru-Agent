using RuAgent.Cli.TerminalGui;
using RuAgent.Client;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace RuAgent.Cli;

public sealed partial class AgentCommands
{
    private sealed partial class ChatSession
    {
        /// <summary>Owns independent conversation workers behind one native application.</summary>
        private sealed class TuiSessionManager(ChatSession initial)
        {
            private const int MaxOpenSessions = 8;
            private readonly IApplication _app = initial.tgApp!;
            private readonly TgSessionHostWindow _window = new();
            private readonly List<ChatSession> _sessions = [];
            private readonly object _sessionsGate = new();
            private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);
            private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(initial.cancellationToken);
            private ChatSession? _active;
            private Task _navigation = Task.CompletedTask;
            private volatile bool _navigating;
            private sealed record ModelDefaults(string Backend, string Model, string Effort);
            private ModelDefaults _defaults = new(initial.backendName, initial.activeModel, initial.activeReasoningEffort);
            private int _stopping;
            private bool Stopping => Volatile.Read(ref _stopping) != 0;
            internal CancellationToken LifetimeToken => _lifetime.Token;

            private ChatSession[] Snapshot() { lock (_sessionsGate) return _sessions.ToArray(); }

            internal async Task RunAsync()
            {
                initial.multiSessions = this;
                initial.cancellationToken = _lifetime.Token;
                initial.CreateTerminalGuiLoop();
                lock (_sessionsGate) _sessions.Add(initial);
                _window.AddPage(initial.tgWindow!);
                Activate(initial);
                _app.Keyboard.KeyDown += OnKey;
                using var stopRegistration = initial.cancellationToken.Register(Stop);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var nextTabsUpdate = 0d;
                _app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
                {
                    if (Stopping || _active is not { } active) return false;
                    Activate(active);
                    if (initial.runSession.FollowableRunId is { } run && initial.submissions.Count == 0
                        && initial.tgWindow!.Composer.Input.Text.Length == 0)
                    {
                        initial.tgWindow.Composer.Input.Text = "/follow " + run;
                        initial.tgLoop!.Submit();
                    }
                    return false;
                });
                var timer = _app.AddTimeout(TimeSpan.FromMilliseconds(Tui.TuiHeader.MeteorFrameMilliseconds), () =>
                {
                    if (Stopping) return false;
                    _window.RefreshPageLayout();
                    _active?.TickSession(clock.Elapsed.TotalSeconds);
                    if (clock.Elapsed.TotalSeconds >= nextTabsUpdate)
                    {
                        UpdateTabs(); nextTabsUpdate = clock.Elapsed.TotalSeconds + .2;
                    }
                    return true;
                });
                try { if (!Stopping) _app.Run(_window); }
                finally
                {
                    Stop();
                    _app.Keyboard.KeyDown -= OnKey;
                    if (timer is not null) _app.RemoveTimeout(timer);
                    try { await _navigation.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                    var sessions = Snapshot();
                    await Task.WhenAll(sessions.Select(session => session.tgLoop!.Completion)).ConfigureAwait(false);
                    await Task.WhenAll(sessions.Select(session => session.tgLoop!.WaitForShutdownSettlementAsync())).ConfigureAwait(false);
                    foreach (var session in sessions)
                    {
                        session.tgLoop!.SetViewActive(null);
                        session.tgWindow!.SetHostedApplication(null);
                        session.submissions.ClearPending();
                        if (ReferenceEquals(session, initial)) continue;
                        session.runSession.Dispose();
                        session.submissions.Dispose();
                    }
                    // The bootstrap still owns the initial page/application. Other
                    // retained pages are owned by this single runnable container.
                    _window.Remove(initial.tgWindow!);
                    _window.Dispose();
                    _lifetime.Dispose();
                }
            }

            private void OnKey(object? sender, Key key)
            {
                if (key.Handled || Stopping) return;
                if (key == Key.F6 || key == Key.F6.WithShift)
                {
                    var sessions = Snapshot();
                    var index = Array.IndexOf(sessions, _active);
                    Activate(sessions[(index + (key.IsShift ? sessions.Length - 1 : 1)) % sessions.Length]);
                    key.Handled = true;
                }
                else if (key == Key.N.WithAlt && _active is { } active)
                {
                    StartNavigation(active, () => active.HandleSessionWorkflowAsync("/new", null));
                    key.Handled = true;
                }
            }

            internal bool TryHandleCommand(ChatSession source, string line)
            {
                ApplyDefaultsIfIdle(source);
                var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return false;
                var command = parts[0].ToLowerInvariant();
                if (command == "/exit") { source.tgWindow!.Composer.Input.Text = string.Empty; Stop(); return true; }
                if (command is not ("/new" or "/sessions")) return false;
                var argument = parts.Length > 1 ? parts[1] : null;
                // Same-session recovery must still pass through its original workflow.
                if (source.historyReloadRequired && argument == "id:" + source.sessionId) return false;
                source.tgWindow!.Composer.Input.Text = string.Empty;
                if (command == "/sessions" && argument == "close")
                {
                    CloseIdle(source);
                    return true;
                }
                StartNavigation(source, () => source.HandleSessionWorkflowAsync(command, argument));
                return true;
            }

            private void StartNavigation(ChatSession source, Func<Task> action)
            {
                if (Stopping || _navigating)
                {
                    source.tgWindow!.Hints.ShowFeedback("session selection is already open", true);
                    return;
                }
                _navigating = true;
                _navigation = NavigateAsync();
                async Task NavigateAsync()
                {
                    try { await action().ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                    catch (Exception error)
                    {
                        if (!Stopping) await OnUi(() => source.tgWindow!.Hints.ShowFeedback(
                            "session_error: " + CliErrorText.ShortMessage(error.Message), true)).ConfigureAwait(false);
                    }
                    finally
                    {
                        _navigating = false;
                        if (!Stopping) await OnUi(CloseDeletedPages).ConfigureAwait(false);
                    }
                }
            }

            internal async Task OpenAsync(ChatSession source, string sessionId)
            {
                if (!ChatSessionWorkflow.IsValidSessionId(sessionId)) throw new InvalidOperationException("invalid_session_id");
                var existing = Snapshot().FirstOrDefault(session => session.sessionId == sessionId);
                if (existing is not null) { await OnUi(() => Activate(existing)).ConfigureAwait(false); return; }
                if (Snapshot().Length >= MaxOpenSessions)
                    throw new InvalidOperationException("session_limit: 8 open tabs · use /sessions close on an idle tab");
                var workspace = await ChatSessionWorkflow.ReadWorkspaceLabelAsync(source._sessionClient, sessionId, _lifetime.Token).ConfigureAwait(false);
                if (workspace is { } recorded && !Path.TrimEndingDirectorySeparator(recorded.Path)
                        .Equals(Path.TrimEndingDirectorySeparator(source.canonicalWorkspace), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("session_workspace_mismatch: open this session from its own workspace");
                var preset = source.cmd.Preset == "plan" ? "plan" : "standard";
                try
                {
                    var stored = await source._agentClient.GetSessionPresetAsync(sessionId, _lifetime.Token).ConfigureAwait(false);
                    if (stored is { } selection) preset = selection.Preset;
                }
                catch (AgentApiException error) when (error.StatusCode == 404) { }

                ChatSession? created = null;
                TgChatWindow? page = null;
                await OnUi(() =>
                {
                    page = TgChatWindow.Build();
                    _window.AddPage(page);
                    var writer = new TgTranscriptWriter(_app, page.Transcript);
                    var submissions = new ChatSubmissionState();
                    var command = source.cmd with { SessionId = sessionId, Images = null,
                        Preset = preset, ConfirmFullAccess = preset == "full_access" };
                    var bootstrap = new ChatBootstrapContext(submissions, writer, source.canonicalWorkspace,
                        source.workspaceLabel, sessionId, source.backendName, source.activeModel, source.activeReasoningEffort,
                        null, source.bootstrap.Engine, true, writer, page, _app);
                    created = new ChatSession(source._host, command, bootstrap, _lifetime.Token) { multiSessions = this };
                }).ConfigureAwait(false);
                try
                {
                    await created!.InitializeAsync().ConfigureAwait(false);
                    _lifetime.Token.ThrowIfCancellationRequested();
                    await OnUi(() =>
                    {
                        created.CreateTerminalGuiLoop();
                        lock (_sessionsGate) _sessions.Add(created);
                        Activate(created);
                        if (created.runSession.FollowableRunId is { } runId)
                        {
                            page!.Composer.Input.Text = "/follow " + runId;
                            created.tgLoop!.Submit();
                        }
                    }).ConfigureAwait(false);
                }
                catch
                {
                    created!.runSession?.Dispose();
                    created.bootstrap.Submissions.Dispose();
                    if (!Stopping) await OnUi(() => { _window.RemovePage(page!); page!.Dispose(); }).ConfigureAwait(false);
                    throw;
                }
            }

            internal async Task<SessionDeleteResult> DeleteAsync(ChatSession source, string sessionId, CancellationToken ct)
            {
                var open = Snapshot().FirstOrDefault(session => session.sessionId == sessionId);
                if (open is not null && !CanRelease(open))
                    return new(false, "session_busy: finish pending work before deleting this session");
                var outcome = await source.DeleteSessionAsync(sessionId, ct).ConfigureAwait(false);
                if (outcome.Deleted) await OnUi(() => _deleted.Add(sessionId)).ConfigureAwait(false);
                return outcome;
            }

            internal Task<string> CreateSessionAsync(ChatSession source, CancellationToken ct)
            {
                if (Snapshot().Length >= MaxOpenSessions)
                    throw new InvalidOperationException("session_limit: 8 open tabs · use /sessions close on an idle tab");
                ct.ThrowIfCancellationRequested();
                return source._sessionClient.CreateAsync(_lifetime.Token);
            }

            private void Activate(ChatSession session)
            {
                if (Stopping) return;
                ApplyDefaultsIfIdle(session);
                _active?.tgLoop!.SetViewActive(null);
                _active?.tgWindow!.SetHostedApplication(null);
                _active = session;
                _window.Activate(session.tgWindow!);
                session.tgLoop!.SetViewActive(_app);
                session.tgWindow!.SetHostedApplication(_app);
                UpdateTabs();
            }

            internal void RememberModelDefaults(ChatSession source) => Volatile.Write(ref _defaults,
                new(source.backendName, source.activeModel, source.activeReasoningEffort));

            private void ApplyDefaultsIfIdle(ChatSession session)
            {
                // Already admitted and queued work keeps the selection captured at submit.
                // Only a fresh idle prompt adopts defaults changed in another tab.
                if (session.tgLoop?.CanCloseSession != true || session.runSession.ParkedRunId is not null) return;
                var selection = Volatile.Read(ref _defaults);
                if (session.backendName == selection.Backend && session.activeModel == selection.Model
                    && session.activeReasoningEffort == selection.Effort) return;
                session.backendName = selection.Backend;
                session.activeModel = selection.Model;
                session.activeReasoningEffort = selection.Effort;
                session.modelUsageRefreshRequired = true;
                session.tgWindow!.Composer.SetMeta(session.cmd.Preset, selection.Model, selection.Backend);
                session.tgWindow.Composer.SetReasoningEffort(selection.Effort);
            }

            private void UpdateTabs() => _window.UpdateTabs(Snapshot().Select(session => new TgSessionTab(
                string.IsNullOrWhiteSpace(session.sessionTitle) ? "session"
                    : ChatDisplayText.Sanitize(session.sessionTitle, 64).Replace('\n', ' ').Replace('\r', ' '),
                session.tgWindow!.IsModalPanelOpen || session.runSession.ParkedRunId is not null ? "?" : session.tgLoop!.IsBusy ? "◐"
                    : session.tgWindow.Hints.FeedbackIsError || session.draftCreationFailed || session.historyReloadRequired
                        || session.tgWindow.Hints.ActiveState == TgHintsState.Paused
                        || session.sessionRenderer.Status.State == Tui.TuiRunState.Failed ? "!" : session.turn > 0 ? "✓" : "·",
                ReferenceEquals(session, _active))).ToArray());

            private void CloseIdle(ChatSession session)
            {
                if (Snapshot().Length <= 1 || !CanRelease(session))
                {
                    session.tgWindow!.Hints.ShowFeedback("keep one tab · finish the run and clear pending attachments before closing", true);
                    return;
                }
                var next = Snapshot().First(other => !ReferenceEquals(other, session));
                Activate(next);
                RemoveIdle(session);
            }

            private static bool CanRelease(ChatSession session) => session.tgLoop!.CanCloseSession
                && !session.attachmentTasks.HasPending
                && session.runSession.ActiveRunId is null && session.runSession.FollowableRunId is null
                && session.runSession.ParkedRunId is null && session.submissions.Count == 0
                && session.pendingHistoryBranch is null && session.submissions.Retryable is null;

            private void CloseDeletedPages()
            {
                foreach (var session in Snapshot().Where(session => _deleted.Contains(session.sessionId)
                             && !ReferenceEquals(session, _active) && !session.tgLoop!.IsBusy
                             && !session.attachmentTasks.HasPending)) RemoveIdle(session);
                UpdateTabs();
            }

            private void RemoveIdle(ChatSession session)
            {
                _ = session.attachmentTasks.StopAsync();
                session.tgLoop!.Stop();
                session.tgWindow!.SetHostedApplication(null);
                _window.RemovePage(session.tgWindow);
                lock (_sessionsGate) _sessions.Remove(session);
                _deleted.Remove(session.sessionId);
                if (!ReferenceEquals(session, initial))
                {
                    session.runSession.Dispose(); session.submissions.Dispose(); session.tgWindow.Dispose();
                }
                UpdateTabs();
            }

            private Task OnUi(Action action)
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var registration = _lifetime.Token.Register(() => completion.TrySetCanceled(_lifetime.Token));
                try
                {
                    _app.Invoke(() =>
                    {
                        if (completion.Task.IsCompleted) return;
                        try { action(); completion.TrySetResult(); }
                        catch (Exception error) { completion.TrySetException(error); }
                    });
                }
                catch (NotInitializedException) { completion.TrySetCanceled(); }
                return Await();
                async Task Await() { try { await completion.Task.ConfigureAwait(false); } finally { registration.Dispose(); } }
            }

            private void Stop() => Stop(0);

            internal void Stop(int exitCode)
            {
                if (exitCode != 0) initial.dispatcherExitCode = exitCode;
                if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
                foreach (var session in Snapshot())
                {
                    session.managedShutdown = true;
                    _ = session.attachmentTasks.StopAsync();
                    session.tgLoop?.Stop();
                }
                _lifetime.Cancel();
                try { _app.Invoke(() => _app.RequestStop()); }
                catch (NotInitializedException) { }
            }
        }
    }
}
