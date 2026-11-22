using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Browser;

/// <summary>
/// Executes the <c>browser</c> tool's actions (ADR 0055): one page per agent session over one
/// isolated Chromium process, session-scoped authority, bounded outputs, and stable named
/// failures. Policy decided whether the call may run; this class decides which page it reaches.
/// </summary>
public sealed class BrowserUseService : IAsyncDisposable
{
    private readonly IBrowserEngine _engine;
    private readonly object _sessionsGate = new();
    private readonly Dictionary<string, BrowserSession> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    public BrowserUseService(IBrowserEngine engine) =>
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public bool IsAvailable => _engine.IsAvailable;

    public async ValueTask<ToolExecutionOutcome> ExecuteAsync(
        string runId,
        string sessionId,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = runId;
        ArgumentNullException.ThrowIfNull(arguments);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Failed("browser_session_required", "The browser needs an agent session.");
        }

        var action = Text(arguments, "action");
        if (action is null || !BrowserActions.All.Contains(action))
        {
            return Failed("browser_action_invalid", "action must be one of " + string.Join(", ", BrowserActions.All) + ".");
        }

        BrowserSession? session;
        lock (_sessionsGate)
        {
            _sessions.TryGetValue(sessionId, out session);
        }

        if (session is null && action is not (BrowserActions.Open or BrowserActions.Close))
        {
            return Failed("browser_not_open", "Call action=open first; the user approves browser control for this session.");
        }

        try
        {
            return action switch
            {
                BrowserActions.Open => await OpenAsync(sessionId, session, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.Navigate => await NavigateAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.Snapshot => await SnapshotAsync(session!, cancellationToken).ConfigureAwait(false),
                BrowserActions.Click => await ClickAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.Type => await TypeAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.Fill => await FillAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.Press => await PressAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.Eval => await EvalAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.Wait => await WaitAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.Console => await ConsoleAsync(session!, cancellationToken).ConfigureAwait(false),
                BrowserActions.Network => await NetworkAsync(session!, cancellationToken).ConfigureAwait(false),
                BrowserActions.Close => await CloseAsync(sessionId, session, cancellationToken).ConfigureAwait(false),
                BrowserActions.Batch => await BatchAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                _ => Failed("browser_action_invalid", "Unknown browser action."),
            };
        }
        catch (BrowserEngineException error)
        {
            return Failed(error.Code, error.Detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    /// <summary>Ends a session's page; a closed or unknown session is ignored.</summary>
    public void RemoveSession(string sessionId)
    {
        BrowserSession? session;
        lock (_sessionsGate)
        {
            if (!_sessions.Remove(sessionId, out session)) return;
        }

        _ = CloseSessionAsync(session);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        BrowserSession[] sessions;
        lock (_sessionsGate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await CloseSessionAsync(session).ConfigureAwait(false);
        }

        await _engine.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<ToolExecutionOutcome> OpenAsync(
        string sessionId,
        BrowserSession? existing,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        Uri? start = null;
        var rawUrl = Text(arguments, "url");
        if (rawUrl is not null)
        {
            if (!TryUrl(rawUrl, out start, out var error))
            {
                return Failed("browser_invalid_url", error);
            }
        }

        if (existing is not null)
        {
            if (start is null) return OpenOutput(existing, start);
            await existing.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await NavigateCoreAsync(existing, start, cancellationToken).ConfigureAwait(false);
                return OpenOutput(existing, start);
            }
            finally
            {
                existing.Gate.Release();
            }
        }

        if (!_engine.IsAvailable)
        {
            return Failed(
                "browser_unavailable",
                "No supported browser was found: install a Chromium browser (Edge, Chrome, Brave, Vivaldi, Opera, Chromium or Arc) or Firefox, or set RU_BROWSER_EXE.");
        }

        var session = new BrowserSession { Headless = Bool(arguments, "headless") ?? true };
        lock (_sessionsGate)
        {
            _sessions[sessionId] = session;
        }

        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                session.Page = await _engine.OpenPageAsync(session.Headless, cancellationToken).ConfigureAwait(false);
                if (start is not null)
                {
                    await NavigateCoreAsync(session, start, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                session.Gate.Release();
            }
        }
        catch (Exception)
        {
            lock (_sessionsGate)
            {
                _sessions.Remove(sessionId);
            }

            session.Gate.Dispose();
            throw;
        }

        return OpenOutput(session, start);
    }

    /// <summary>
    /// Runs up to <see cref="BrowserLimits.MaxBatchSteps"/> page actions in one tool call so a
    /// model can fill, click and verify without paying one model step per action. Steps run in
    /// order on the session's open page and stop at the first failure; open and close stay
    /// separate because they carry the session's approval and lifetime.
    /// </summary>
    private async ValueTask<ToolExecutionOutcome> BatchAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (arguments["steps"] is not JsonArray steps || steps.Count == 0)
        {
            return Failed("browser_arguments_invalid", "batch needs steps: 1 to " + BrowserLimits.MaxBatchSteps + " page actions.");
        }

        if (steps.Count > BrowserLimits.MaxBatchSteps)
        {
            return Failed("browser_arguments_invalid", "batch accepts at most " + BrowserLimits.MaxBatchSteps + " steps.");
        }

        var parsed = new List<(string Action, JsonObject Arguments)>(steps.Count);
        foreach (var item in steps)
        {
            if (item is not JsonObject step)
            {
                return Failed("browser_arguments_invalid", "each batch step must be an object such as {\"action\":\"click\",\"ref\":\"e3\"}.");
            }

            var stepAction = Text(step, "action");
            if (stepAction is null || !BrowserActions.Batchable.Contains(stepAction))
            {
                return Failed(
                    "browser_arguments_invalid",
                    "batch steps may use " + string.Join(", ", BrowserActions.Batchable) + "; open, close and batch cannot be nested.");
            }

            parsed.Add((stepAction, step));
        }

        var results = new JsonArray();
        for (var index = 0; index < parsed.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (stepAction, stepArguments) = parsed[index];
            ToolExecutionOutcome step;
            try
            {
                step = await ExecuteSingleAsync(session, stepAction, stepArguments, cancellationToken).ConfigureAwait(false);
            }
            catch (BrowserEngineException engineError)
            {
                return Failed(engineError.Code, "Step " + (index + 1) + " (" + stepAction + "): " + engineError.Detail);
            }

            if (step.ErrorCode is { } error)
            {
                return Failed(error, "Step " + (index + 1) + " (" + stepAction + "): " + step.SafeSummary);
            }

            var output = step.Output.DeepClone() as JsonObject ?? new JsonObject();
            output["step"] = index + 1;
            results.Add(output);
        }

        return Success(
            new JsonObject
            {
                ["action"] = BrowserActions.Batch,
                ["step_count"] = results.Count,
                ["steps"] = results,
            },
            "Ran " + results.Count + " browser steps.");
    }

    private ValueTask<ToolExecutionOutcome> ExecuteSingleAsync(
        BrowserSession session,
        string action,
        JsonObject arguments,
        CancellationToken cancellationToken) => action switch
    {
        BrowserActions.Navigate => NavigateAsync(session, arguments, cancellationToken),
        BrowserActions.Snapshot => SnapshotAsync(session, cancellationToken),
        BrowserActions.Click => ClickAsync(session, arguments, cancellationToken),
        BrowserActions.Type => TypeAsync(session, arguments, cancellationToken),
        BrowserActions.Fill => FillAsync(session, arguments, cancellationToken),
        BrowserActions.Press => PressAsync(session, arguments, cancellationToken),
        BrowserActions.Eval => EvalAsync(session, arguments, cancellationToken),
        BrowserActions.Wait => WaitAsync(session, arguments, cancellationToken),
        BrowserActions.Console => ConsoleAsync(session, cancellationToken),
        BrowserActions.Network => NetworkAsync(session, cancellationToken),
        _ => ValueTask.FromResult(Failed("browser_action_invalid", "Unknown batch step action.")),
    };

    private async ValueTask<ToolExecutionOutcome> NavigateAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var rawUrl = Text(arguments, "url");
        if (rawUrl is null)
        {
            return Failed("browser_invalid_url", "navigate needs url.");
        }

        if (!TryUrl(rawUrl, out var url, out var error))
        {
            return Failed("browser_invalid_url", error);
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await NavigateCoreAsync(session, url!, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.Navigate,
                    ["url"] = url!.ToString(),
                    ["title"] = await TitleAsync(session, cancellationToken).ConfigureAwait(false),
                },
                "Browser navigated.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async ValueTask<ToolExecutionOutcome> SnapshotAsync(
        BrowserSession session,
        CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await _engine.SnapshotAsync(
                RequirePage(session),
                BrowserLimits.MaxSnapshotElements,
                BrowserLimits.MaxSnapshotTextChars,
                cancellationToken).ConfigureAwait(false);
            var elements = new JsonArray();
            foreach (var element in snapshot.Elements)
            {
                var entry = new JsonObject
                {
                    ["ref"] = element.ElementRef,
                    ["role"] = element.Role,
                    ["name"] = element.Name,
                };
                if (element.Value.Length > 0) entry["value"] = element.Value;
                if (element.Checked) entry["checked"] = true;
                if (element.Disabled) entry["disabled"] = true;
                elements.Add(entry);
            }

            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.Snapshot,
                    ["url"] = snapshot.Url,
                    ["title"] = snapshot.Title,
                    ["elements"] = elements,
                    ["text"] = snapshot.Text,
                    ["text_truncated"] = snapshot.TextTruncated,
                    ["element_count"] = elements.Count,
                },
                "Page snapshot captured.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async ValueTask<ToolExecutionOutcome> ClickAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var elementRef = Text(arguments, "ref");
        if (elementRef is null)
        {
            return Failed("browser_arguments_invalid", "click needs ref from a snapshot.");
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _engine.ClickAsync(RequirePage(session), elementRef, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject { ["action"] = BrowserActions.Click, ["ref"] = elementRef },
                "Clicked " + elementRef + ".");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async ValueTask<ToolExecutionOutcome> TypeAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (!TryText(arguments, "text", BrowserLimits.MaxTextChars, out var text, out var error))
        {
            return Failed("browser_arguments_invalid", error);
        }

        var elementRef = Text(arguments, "ref");
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _engine.TypeAsync(RequirePage(session), elementRef, text!, replace: false, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.Type,
                    ["ref"] = elementRef,
                    ["characters"] = text!.Length,
                },
                "Typed text.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async ValueTask<ToolExecutionOutcome> FillAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var elementRef = Text(arguments, "ref");
        if (elementRef is null)
        {
            return Failed("browser_arguments_invalid", "fill needs ref from a snapshot.");
        }

        if (!TryText(arguments, "text", BrowserLimits.MaxTextChars, out var text, out var error))
        {
            return Failed("browser_arguments_invalid", error);
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _engine.FillAsync(RequirePage(session), elementRef, text!, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.Fill,
                    ["ref"] = elementRef,
                    ["characters"] = text!.Length,
                },
                "Filled " + elementRef + ".");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async ValueTask<ToolExecutionOutcome> PressAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var key = Text(arguments, "key");
        if (key is null || key.Length > BrowserLimits.MaxKeyChars)
        {
            return Failed("browser_arguments_invalid", "press needs key such as Enter, Tab or Escape.");
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _engine.PressAsync(RequirePage(session), key, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject { ["action"] = BrowserActions.Press, ["key"] = key },
                "Pressed " + key + ".");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async ValueTask<ToolExecutionOutcome> EvalAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var expression = Text(arguments, "expression");
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > BrowserLimits.MaxExpressionChars)
        {
            return Failed(
                "browser_arguments_invalid",
                "eval needs expression of 1 to " + BrowserLimits.MaxExpressionChars + " characters.");
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var json = await _engine.EvaluateAsync(RequirePage(session), expression, cancellationToken).ConfigureAwait(false);
            JsonNode? result;
            try
            {
                result = JsonNode.Parse(json);
            }
            catch (System.Text.Json.JsonException)
            {
                result = JsonValue.Create(json);
            }

            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.Eval,
                    ["result"] = result?.DeepClone(),
                },
                "Evaluated page script.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async ValueTask<ToolExecutionOutcome> WaitAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var milliseconds = Int(arguments, "ms") ?? 1000;
        if (milliseconds is < BrowserLimits.MinWaitMilliseconds or > BrowserLimits.MaxWaitMilliseconds)
        {
            return Failed(
                "browser_arguments_invalid",
                "ms must be between " + BrowserLimits.MinWaitMilliseconds + " and " + BrowserLimits.MaxWaitMilliseconds + ".");
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject { ["action"] = BrowserActions.Wait, ["waited_ms"] = milliseconds },
                "Waited " + milliseconds + " ms.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async ValueTask<ToolExecutionOutcome> ConsoleAsync(
        BrowserSession session,
        CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await _engine.ConsoleAsync(
                RequirePage(session),
                BrowserLimits.MaxConsoleMessages,
                cancellationToken).ConfigureAwait(false);
            var messages = new JsonArray();
            foreach (var entry in entries)
            {
                messages.Add(new JsonObject { ["level"] = entry.Level, ["text"] = entry.Text });
            }

            return Success(
                new JsonObject { ["action"] = BrowserActions.Console, ["messages"] = messages },
                "Read browser console.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async ValueTask<ToolExecutionOutcome> NetworkAsync(
        BrowserSession session,
        CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await _engine.NetworkAsync(
                RequirePage(session),
                BrowserLimits.MaxNetworkRequests,
                cancellationToken).ConfigureAwait(false);
            var requests = new JsonArray();
            foreach (var entry in entries)
            {
                requests.Add(new JsonObject
                {
                    ["method"] = entry.Method,
                    ["url"] = entry.Url,
                    ["status"] = entry.Status,
                    ["type"] = entry.ResourceType,
                });
            }

            return Success(
                new JsonObject { ["action"] = BrowserActions.Network, ["requests"] = requests },
                "Read browser network log.");
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private async ValueTask<ToolExecutionOutcome> CloseAsync(
        string sessionId,
        BrowserSession? session,
        CancellationToken cancellationToken)
    {
        if (session is null)
        {
            return Success(
                new JsonObject { ["action"] = BrowserActions.Close, ["closed"] = false },
                "Browser was not open.");
        }

        lock (_sessionsGate)
        {
            _sessions.Remove(sessionId);
        }

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Page is { } page)
            {
                session.Page = null;
                await _engine.ClosePageAsync(page, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            session.Gate.Release();
            session.Gate.Dispose();
        }

        return Success(
            new JsonObject { ["action"] = BrowserActions.Close, ["closed"] = true },
            "Browser page closed.");
    }

    private async Task NavigateCoreAsync(BrowserSession session, Uri url, CancellationToken cancellationToken)
    {
        await _engine.NavigateAsync(RequirePage(session), url.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TitleAsync(BrowserSession session, CancellationToken cancellationToken)
    {
        try
        {
            return await _engine.TitleAsync(RequirePage(session), cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserEngineException)
        {
            return string.Empty;
        }
    }

    private static BrowserPageHandle RequirePage(BrowserSession session) =>
        session.Page ?? throw new BrowserEngineException("browser_not_open", "The page is closed; call action=open again.");

    private async Task CloseSessionAsync(BrowserSession session)
    {
        try
        {
            await session.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (session.Page is { } page)
                {
                    session.Page = null;
                    await _engine.ClosePageAsync(page, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                session.Gate.Release();
            }
        }
        catch (Exception)
        {
            // Session teardown is best effort: the Host or the browser is already going away.
        }
        finally
        {
            session.Gate.Dispose();
        }
    }

    private ToolExecutionOutcome OpenOutput(BrowserSession session, Uri? start)
    {
        _ = session;
        return Success(
            new JsonObject
            {
                ["action"] = BrowserActions.Open,
                ["opened"] = true,
                ["url"] = start?.ToString() ?? "about:blank",
            },
            "Browser opened.");
    }

    private static ToolExecutionOutcome Success(JsonObject output, string summary) =>
        ToolExecutionOutcome.Succeeded(output, ToolVerificationStates.NotRequired, summary);

    private static ToolExecutionOutcome Failed(string code, string detail) =>
        ToolExecutionOutcome.Failed(code, detail);

    private static bool TryUrl(string raw, out Uri? url, out string error)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > BrowserLimits.MaxUrlChars)
        {
            error = "url must be 1 to " + BrowserLimits.MaxUrlChars + " characters.";
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host))
        {
            error = "url must be an absolute http or https address.";
            return false;
        }

        url = parsed;
        error = string.Empty;
        return true;
    }

    private static bool TryText(JsonObject arguments, string name, int maxChars, out string? text, out string error)
    {
        text = Text(arguments, name);
        if (string.IsNullOrEmpty(text) || text.Length > maxChars)
        {
            error = name + " must be 1 to " + maxChars + " characters.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static string? Text(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static bool? Bool(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private sealed class BrowserSession
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public BrowserPageHandle? Page { get; set; }

        public bool Headless { get; init; }
    }
}
