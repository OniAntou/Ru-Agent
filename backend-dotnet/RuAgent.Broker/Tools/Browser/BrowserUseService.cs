using System.Text.Json.Nodes;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Browser;

/// <summary>
/// What the Host decided for one <c>browser</c> call outside the model's arguments: whether the
/// pinned model accepts images, the workspace files an approved <c>upload</c> resolved to, and
/// whether the run pinned the persistent Ru profile (ADR 0055, amended 2026-11-25).
/// </summary>
public sealed record BrowserCallOptions(
    bool ImageInputAccepted = false,
    IReadOnlyList<string>? UploadPaths = null,
    bool PersistentProfile = false);

/// <summary>
/// Executes the <c>browser</c> tool's actions (ADR 0055): up to <see cref="BrowserLimits.MaxTabs"/>
/// tabs per agent session over one isolated browser process, session-scoped authority, bounded
/// outputs, and stable named failures. Policy decided whether the call may run; this class decides
/// which page it reaches.
/// </summary>
public sealed class BrowserUseService : IAsyncDisposable
{
    private static readonly TimeSpan HistoryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReloadTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NavigationPoll = TimeSpan.FromMilliseconds(150);

    private readonly IBrowserEngine _engine;
    private readonly IBrowserEngine? _persistentEngine;
    private readonly object _sessionsGate = new();
    private readonly Dictionary<string, BrowserSession> _sessions = new(StringComparer.Ordinal);
    private int _disposed;

    /// <param name="engine">The engine with an ephemeral profile per browser process.</param>
    /// <param name="persistentEngine">The engine over Ru's persistent profile, for runs that pinned it.</param>
    public BrowserUseService(IBrowserEngine engine, IBrowserEngine? persistentEngine = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _persistentEngine = persistentEngine;
    }

    public bool IsAvailable => _engine.IsAvailable;

    public async ValueTask<ToolExecutionOutcome> ExecuteAsync(
        string runId,
        string sessionId,
        JsonObject arguments,
        CancellationToken cancellationToken,
        BrowserCallOptions? options = null)
    {
        _ = runId;
        ArgumentNullException.ThrowIfNull(arguments);
        options ??= new BrowserCallOptions();
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

        ToolExecutionOutcome outcome;
        try
        {
            outcome = action switch
            {
                BrowserActions.Open => await OpenAsync(sessionId, session, arguments, options, cancellationToken).ConfigureAwait(false),
                BrowserActions.Close => await CloseAsync(sessionId, session, cancellationToken).ConfigureAwait(false),
                BrowserActions.Batch => await BatchAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.Screenshot => await ScreenshotAsync(session!, arguments, options, cancellationToken).ConfigureAwait(false),
                BrowserActions.Tabs => await TabsAsync(session!, cancellationToken).ConfigureAwait(false),
                BrowserActions.NewTab => await NewTabAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.SwitchTab => await SwitchTabAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.CloseTab => await CloseTabAsync(session!, arguments, cancellationToken).ConfigureAwait(false),
                BrowserActions.Upload => await UploadAsync(session!, arguments, options, cancellationToken).ConfigureAwait(false),
                _ => await ExecuteSingleAsync(session!, action, arguments, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (BrowserEngineException error)
        {
            outcome = Failed(error.Code, error.Detail);
        }

        if (action is not BrowserActions.Close && session is not null)
        {
            Annotate(session, outcome);
        }
        else if (action is BrowserActions.Open)
        {
            lock (_sessionsGate)
            {
                _sessions.TryGetValue(sessionId, out session);
            }

            if (session is not null) Annotate(session, outcome);
        }

        return outcome;
    }

    /// <summary>Ends a session's tabs; a closed or unknown session is ignored.</summary>
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
        if (_persistentEngine is not null) await _persistentEngine.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<ToolExecutionOutcome> OpenAsync(
        string sessionId,
        BrowserSession? existing,
        JsonObject arguments,
        BrowserCallOptions options,
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

        var engine = options.PersistentProfile && _persistentEngine is not null ? _persistentEngine : _engine;
        if (!engine.IsAvailable)
        {
            return Failed(
                "browser_unavailable",
                "No supported browser was found: install a Chromium browser (Edge, Chrome, Brave, Vivaldi, Opera, Chromium or Arc) or Firefox, or set RU_BROWSER_EXE.");
        }

        var session = new BrowserSession(engine, Bool(arguments, "headless") ?? true, engine == _persistentEngine);
        lock (_sessionsGate)
        {
            _sessions[sessionId] = session;
        }

        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                session.Tabs.Add(await engine.OpenPageAsync(session.Headless, cancellationToken).ConfigureAwait(false));
                session.Active = 0;
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

            await CloseSessionAsync(session).ConfigureAwait(false);
            throw;
        }

        return OpenOutput(session, start);
    }

    /// <summary>
    /// Runs up to <see cref="BrowserLimits.MaxBatchSteps"/> page actions in one tool call so a
    /// model can fill, click and verify without paying one model step per action. Steps run in
    /// order on the session's active tab and stop at the first failure; open, close, tab actions,
    /// screenshots and uploads stay separate because they carry approval, lifetime or an image.
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
                    "batch steps may use " + string.Join(", ", BrowserActions.Batchable) + "; open, close, batch, screenshot, tab actions and upload cannot be nested.");
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
        BrowserActions.Scroll => ScrollAsync(session, arguments, cancellationToken),
        BrowserActions.Hover => HoverAsync(session, arguments, cancellationToken),
        BrowserActions.Select => SelectAsync(session, arguments, cancellationToken),
        BrowserActions.Back or BrowserActions.Forward or BrowserActions.Reload => HistoryAsync(session, action, cancellationToken),
        BrowserActions.Find => FindAsync(session, arguments, cancellationToken),
        BrowserActions.Dialog => DialogAsync(session, arguments, cancellationToken),
        _ => ValueTask.FromResult(Failed("browser_action_invalid", "Unknown browser action.")),
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

        return await LockedAsync(session, async () =>
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
        }, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ToolExecutionOutcome> SnapshotAsync(BrowserSession session, CancellationToken cancellationToken) =>
        LockedAsync(session, async () =>
        {
            var snapshot = await session.Engine.SnapshotAsync(
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

            var output = new JsonObject
            {
                ["action"] = BrowserActions.Snapshot,
                ["url"] = snapshot.Url,
                ["title"] = snapshot.Title,
                ["elements"] = elements,
                ["text"] = snapshot.Text,
                ["text_truncated"] = snapshot.TextTruncated,
                ["element_count"] = elements.Count,
            };
            if (session.Tabs.Count > 1) output["tab"] = session.Active + 1;
            return Success(output, "Page snapshot captured.");
        }, cancellationToken);

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

        return await LockedAsync(session, async () =>
        {
            await session.Engine.ClickAsync(RequirePage(session), elementRef, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject { ["action"] = BrowserActions.Click, ["ref"] = elementRef },
                "Clicked " + elementRef + ".");
        }, cancellationToken).ConfigureAwait(false);
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
        return await LockedAsync(session, async () =>
        {
            await session.Engine.TypeAsync(RequirePage(session), elementRef, text!, replace: false, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.Type,
                    ["ref"] = elementRef,
                    ["characters"] = text!.Length,
                },
                "Typed text.");
        }, cancellationToken).ConfigureAwait(false);
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

        return await LockedAsync(session, async () =>
        {
            await session.Engine.FillAsync(RequirePage(session), elementRef, text!, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.Fill,
                    ["ref"] = elementRef,
                    ["characters"] = text!.Length,
                },
                "Filled " + elementRef + ".");
        }, cancellationToken).ConfigureAwait(false);
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

        return await LockedAsync(session, async () =>
        {
            await session.Engine.PressAsync(RequirePage(session), key, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject { ["action"] = BrowserActions.Press, ["key"] = key },
                "Pressed " + key + ".");
        }, cancellationToken).ConfigureAwait(false);
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

        return await LockedAsync(session, async () =>
        {
            var result = await EvaluateJsonAsync(session, expression, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.Eval,
                    ["result"] = result?.DeepClone(),
                },
                "Evaluated page script.");
        }, cancellationToken).ConfigureAwait(false);
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

        return await LockedAsync(session, async () =>
        {
            await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject { ["action"] = BrowserActions.Wait, ["waited_ms"] = milliseconds },
                "Waited " + milliseconds + " ms.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ToolExecutionOutcome> ConsoleAsync(BrowserSession session, CancellationToken cancellationToken) =>
        LockedAsync(session, async () =>
        {
            var entries = await session.Engine.ConsoleAsync(
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
        }, cancellationToken);

    private ValueTask<ToolExecutionOutcome> NetworkAsync(BrowserSession session, CancellationToken cancellationToken) =>
        LockedAsync(session, async () =>
        {
            var entries = await session.Engine.NetworkAsync(
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
        }, cancellationToken);

    private async ValueTask<ToolExecutionOutcome> ScreenshotAsync(
        BrowserSession session,
        JsonObject arguments,
        BrowserCallOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.ImageInputAccepted)
        {
            return Failed(
                "image_unsupported_by_model",
                "The pinned model does not accept images; use action=snapshot or find to read the page.");
        }

        var elementRef = Text(arguments, "ref");
        return await LockedAsync(session, async () =>
        {
            var png = await session.Engine.ScreenshotAsync(RequirePage(session), elementRef, cancellationToken).ConfigureAwait(false);
            if (png.Length > ToolOutputImage.MaximumBytes)
            {
                return Failed("browser_screenshot_failed", "The screenshot is larger than a tool image may be; screenshot one ref instead.");
            }

            var output = new JsonObject
            {
                ["action"] = BrowserActions.Screenshot,
                ["media_type"] = "image/png",
            };
            if (elementRef is not null) output["ref"] = elementRef;
            if (PngSize(png) is { } size)
            {
                output["width"] = size.Width;
                output["height"] = size.Height;
            }

            if (session.Tabs.Count > 1) output["tab"] = session.Active + 1;
            return new ToolExecutionOutcome(
                ToolResultStatuses.Succeeded,
                output,
                null,
                "Browser screenshot captured.",
                ToolVerificationStates.NotRequired,
                Image: new ToolOutputImage("image/png", png));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ToolExecutionOutcome> ScrollAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var elementRef = Text(arguments, "ref");
        int? index = elementRef is null ? null : BrowserPageScripts.RefIndex(elementRef);
        var direction = Text(arguments, "direction") ?? "down";
        if (direction is not ("up" or "down" or "left" or "right"))
        {
            return Failed("browser_arguments_invalid", "direction must be up, down, left or right.");
        }

        var amount = Int(arguments, "amount") ?? 5;
        if (amount is < 1 or > BrowserLimits.MaxScrollAmount)
        {
            return Failed("browser_arguments_invalid", "amount must be 1 to " + BrowserLimits.MaxScrollAmount + " steps of " + BrowserLimits.ScrollStepPixels + " px.");
        }

        return await LockedAsync(session, async () =>
        {
            var result = await EvaluateJsonAsync(session, BrowserPageScripts.Scroll(index, direction, amount), cancellationToken).ConfigureAwait(false) as JsonObject;
            if (result is null)
            {
                throw new BrowserEngineException("browser_element_not_found", elementRef + " is not on the page; take a new snapshot.");
            }

            var output = new JsonObject
            {
                ["action"] = BrowserActions.Scroll,
                ["x"] = result["x"]?.DeepClone(),
                ["y"] = result["y"]?.DeepClone(),
            };
            if (elementRef is not null) output["ref"] = elementRef;
            else output["at_end"] = BrowserPageScripts.BoolOf(result, "at_end");
            return Success(output, elementRef is null ? "Scrolled " + direction + "." : "Scrolled " + elementRef + " into view.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ToolExecutionOutcome> HoverAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var elementRef = Text(arguments, "ref");
        if (elementRef is null)
        {
            return Failed("browser_arguments_invalid", "hover needs ref from a snapshot.");
        }

        return await LockedAsync(session, async () =>
        {
            await session.Engine.HoverAsync(RequirePage(session), elementRef, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject { ["action"] = BrowserActions.Hover, ["ref"] = elementRef },
                "Hovered " + elementRef + ".");
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ToolExecutionOutcome> SelectAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var elementRef = Text(arguments, "ref");
        var value = Text(arguments, "value");
        if (elementRef is null || string.IsNullOrEmpty(value) || value.Length > BrowserLimits.MaxSelectValueChars)
        {
            return Failed("browser_arguments_invalid", "select needs ref of a <select> and value, an option's value or label.");
        }

        var index = BrowserPageScripts.RefIndex(elementRef);
        return await LockedAsync(session, async () =>
        {
            var result = await EvaluateJsonAsync(session, BrowserPageScripts.Select(index, value), cancellationToken).ConfigureAwait(false) as JsonObject;
            if (!BrowserPageScripts.BoolOf(result, "ok"))
            {
                return BrowserPageScripts.TextOf(result, "reason") switch
                {
                    "not_select" => Failed("browser_select_invalid", elementRef + " is not a <select>; click custom dropdowns instead."),
                    "no_option" => Failed("browser_select_invalid", "No option of " + elementRef + " matches; options: " + string.Join(", ", (result?["options"] as JsonArray ?? []).Select(item => item?.ToString()))),
                    "disabled" => Failed("browser_select_invalid", "That option of " + elementRef + " is disabled."),
                    _ => Failed("browser_element_not_found", elementRef + " is not on the page; take a new snapshot."),
                };
            }

            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.Select,
                    ["ref"] = elementRef,
                    ["value"] = BrowserPageScripts.TextOf(result, "value"),
                    ["label"] = BrowserPageScripts.TextOf(result, "label"),
                },
                "Selected an option of " + elementRef + ".");
        }, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ToolExecutionOutcome> HistoryAsync(BrowserSession session, string move, CancellationToken cancellationToken) =>
        LockedAsync(session, async () =>
        {
            var start = await EvaluateJsonAsync(session, BrowserPageScripts.StartNavigation(move), cancellationToken).ConfigureAwait(false) as JsonObject;
            var token = BrowserPageScripts.TextOf(start, "token");
            var href = BrowserPageScripts.TextOf(start, "href");
            var deadline = DateTime.UtcNow + (move == BrowserActions.Reload ? ReloadTimeout : HistoryTimeout);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(NavigationPoll, cancellationToken).ConfigureAwait(false);
                JsonObject? state;
                try
                {
                    state = await EvaluateJsonAsync(session, BrowserPageScripts.NavigationSettled(token, href), cancellationToken).ConfigureAwait(false) as JsonObject;
                }
                catch (BrowserEngineException error) when (error.Code is "browser_eval_failed" or "browser_timeout")
                {
                    // The old document is going away; the next poll reads the new one.
                    continue;
                }

                if (BrowserPageScripts.BoolOf(state, "done"))
                {
                    return Success(
                        new JsonObject
                        {
                            ["action"] = move,
                            ["url"] = BrowserPageScripts.TextOf(state, "url"),
                            ["title"] = BrowserPageScripts.TextOf(state, "title"),
                        },
                        "Browser went " + move + ".");
                }
            }

            return Failed(
                "browser_timeout",
                move == BrowserActions.Reload
                    ? "The page did not finish reloading in time; take a snapshot."
                    : "The page did not change in time; there may be no page to go " + move + " to, or it is still loading.");
        }, cancellationToken);

    private async ValueTask<ToolExecutionOutcome> FindAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var query = Text(arguments, "query");
        if (string.IsNullOrWhiteSpace(query) || query.Length > BrowserLimits.MaxFindQueryChars)
        {
            return Failed("browser_arguments_invalid", "find needs query of 1 to " + BrowserLimits.MaxFindQueryChars + " characters.");
        }

        return await LockedAsync(session, async () =>
        {
            var result = await EvaluateJsonAsync(session, BrowserPageScripts.Find(query.Trim(), BrowserLimits.MaxFindResults), cancellationToken).ConfigureAwait(false) as JsonObject;
            var matches = new JsonArray();
            foreach (var item in result?["matches"] as JsonArray ?? [])
            {
                if (item is not JsonObject match) continue;
                matches.Add(new JsonObject
                {
                    ["ref"] = BrowserPageScripts.TextOf(match, "ref"),
                    ["role"] = BrowserPageScripts.TextOf(match, "role"),
                    ["name"] = BrowserPageScripts.TextOf(match, "name"),
                });
            }

            var total = result?["total"] is JsonValue totalValue && totalValue.TryGetValue<int>(out var count) ? count : matches.Count;
            var output = new JsonObject
            {
                ["action"] = BrowserActions.Find,
                ["query"] = query,
                ["matches"] = matches,
            };
            if (total > matches.Count) output["truncated_from"] = total;
            return Success(output, "Found " + matches.Count + " matches.");
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ToolExecutionOutcome> DialogAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (Bool(arguments, "accept") is not { } accept)
        {
            return Failed("browser_arguments_invalid", "dialog needs accept: true or false.");
        }

        var text = Text(arguments, "text");
        if (text is not null && text.Length > BrowserLimits.MaxDialogTextChars)
        {
            return Failed("browser_arguments_invalid", "text for a prompt dialog may have at most " + BrowserLimits.MaxDialogTextChars + " characters.");
        }

        return await LockedAsync(session, () =>
        {
            session.Engine.SetNextDialogAnswer(RequirePage(session), accept, text);
            return Task.FromResult(Success(
                new JsonObject { ["action"] = BrowserActions.Dialog, ["accept"] = accept },
                "The next dialog will be " + (accept ? "accepted." : "dismissed.")));
        }, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ToolExecutionOutcome> TabsAsync(BrowserSession session, CancellationToken cancellationToken) =>
        LockedAsync(session, async () =>
        {
            var opened = new JsonArray();
            AdoptOpenedPages(session, opened);
            var tabs = new JsonArray();
            for (var index = 0; index < session.Tabs.Count; index++)
            {
                var entry = new JsonObject { ["tab"] = index + 1 };
                try
                {
                    var info = await session.Engine.EvaluateAsync(
                        session.Tabs[index],
                        "({url: location.href, title: document.title || ''})",
                        cancellationToken).ConfigureAwait(false);
                    var parsed = JsonNode.Parse(info) as JsonObject;
                    entry["url"] = BrowserPageScripts.TextOf(parsed, "url");
                    entry["title"] = BrowserPageScripts.TextOf(parsed, "title");
                }
                catch (Exception error) when (error is BrowserEngineException or System.Text.Json.JsonException)
                {
                    entry["unavailable"] = true;
                }

                if (index == session.Active) entry["active"] = true;
                tabs.Add(entry);
            }

            var output = new JsonObject { ["action"] = BrowserActions.Tabs, ["tabs"] = tabs };
            if (opened.Count > 0) output["new_tabs"] = opened;
            return Success(output, "Listed " + tabs.Count + " tabs.");
        }, cancellationToken);

    private async ValueTask<ToolExecutionOutcome> NewTabAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        Uri? start = null;
        if (Text(arguments, "url") is { } rawUrl && !TryUrl(rawUrl, out start, out var error))
        {
            return Failed("browser_invalid_url", error);
        }

        return await LockedAsync(session, async () =>
        {
            if (session.Tabs.Count >= BrowserLimits.MaxTabs)
            {
                return Failed("browser_tab_limit", "A session may have at most " + BrowserLimits.MaxTabs + " tabs; close one first.");
            }

            session.Tabs.Add(await session.Engine.OpenPageAsync(session.Headless, cancellationToken).ConfigureAwait(false));
            session.Active = session.Tabs.Count - 1;
            if (start is not null) await NavigateCoreAsync(session, start, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.NewTab,
                    ["tab"] = session.Active + 1,
                    ["url"] = start?.ToString() ?? "about:blank",
                },
                "Opened tab " + (session.Active + 1) + ".");
        }, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<ToolExecutionOutcome> SwitchTabAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken) =>
        LockedAsync(session, () =>
        {
            var opened = new JsonArray();
            AdoptOpenedPages(session, opened);
            if (Int(arguments, "tab") is not { } tab || tab < 1 || tab > session.Tabs.Count)
            {
                return Task.FromResult(Failed("browser_tab_invalid", "tab must be 1 to " + session.Tabs.Count + "; call action=tabs to list them."));
            }

            session.Active = tab - 1;
            var output = new JsonObject { ["action"] = BrowserActions.SwitchTab, ["tab"] = tab };
            if (opened.Count > 0) output["new_tabs"] = opened;
            return Task.FromResult(Success(output, "Switched to tab " + tab + "."));
        }, cancellationToken);

    private ValueTask<ToolExecutionOutcome> CloseTabAsync(
        BrowserSession session,
        JsonObject arguments,
        CancellationToken cancellationToken) =>
        LockedAsync(session, async () =>
        {
            var tab = Int(arguments, "tab") ?? session.Active + 1;
            if (tab < 1 || tab > session.Tabs.Count)
            {
                return Failed("browser_tab_invalid", "tab must be 1 to " + session.Tabs.Count + "; call action=tabs to list them.");
            }

            if (session.Tabs.Count == 1)
            {
                return Failed("browser_tab_invalid", "This is the session's only tab; use action=close to end the browser session.");
            }

            var page = session.Tabs[tab - 1];
            session.Tabs.RemoveAt(tab - 1);
            if (session.Active >= tab - 1 && session.Active > 0) session.Active--;
            await ClosePageQuietlyAsync(session.Engine, page).ConfigureAwait(false);
            return Success(
                new JsonObject { ["action"] = BrowserActions.CloseTab, ["closed_tab"] = tab, ["tab"] = session.Active + 1 },
                "Closed tab " + tab + ".");
        }, cancellationToken);

    private async ValueTask<ToolExecutionOutcome> UploadAsync(
        BrowserSession session,
        JsonObject arguments,
        BrowserCallOptions options,
        CancellationToken cancellationToken)
    {
        var elementRef = Text(arguments, "ref");
        if (elementRef is null)
        {
            return Failed("browser_arguments_invalid", "upload needs ref of an <input type=file> and paths.");
        }

        if (options.UploadPaths is not { Count: > 0 } paths || paths.Count > BrowserLimits.MaxUploadFiles)
        {
            return Failed("browser_upload_paths_required", "upload needs paths: 1 to " + BrowserLimits.MaxUploadFiles + " workspace files the user approved.");
        }

        return await LockedAsync(session, async () =>
        {
            await session.Engine.SetFilesAsync(RequirePage(session), elementRef, paths, cancellationToken).ConfigureAwait(false);
            return Success(
                new JsonObject
                {
                    ["action"] = BrowserActions.Upload,
                    ["ref"] = elementRef,
                    ["files"] = paths.Count,
                },
                "Set " + paths.Count + " files on " + elementRef + ".");
        }, cancellationToken).ConfigureAwait(false);
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
            var tabs = WithUnadoptedPopups(session);
            session.Tabs.Clear();
            foreach (var page in tabs)
            {
                await ClosePageQuietlyAsync(session.Engine, page).ConfigureAwait(false);
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

    /// <summary>
    /// Adds what happened on the session's pages beside the action itself: popups the page opened
    /// become session tabs (up to the limit), and every JavaScript dialog the engine answered is
    /// reported, so the Agent learns about a dismissed confirm or a new window on the next result.
    /// </summary>
    private void Annotate(BrowserSession session, ToolExecutionOutcome outcome)
    {
        if (!session.Gate.Wait(0)) return;
        try
        {
            var opened = new JsonArray();
            AdoptOpenedPages(session, opened);
            var dialogs = new JsonArray();
            for (var index = 0; index < session.Tabs.Count; index++)
            {
                foreach (var dialog in session.Engine.TakeDialogs(session.Tabs[index]))
                {
                    var entry = new JsonObject
                    {
                        ["kind"] = dialog.Kind,
                        ["message"] = dialog.Message,
                        ["accepted"] = dialog.Accepted,
                    };
                    if (session.Tabs.Count > 1) entry["tab"] = index + 1;
                    dialogs.Add(entry);
                }
            }

            if (opened.Count > 0) outcome.Output["new_tabs"] = opened;
            if (dialogs.Count > 0) outcome.Output["dialogs"] = dialogs;
        }
        catch (ObjectDisposedException)
        {
            // The session closed while the result was being prepared.
        }
        finally
        {
            try
            {
                session.Gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Closing disposed the gate; there is nothing left to annotate.
            }
        }
    }

    /// <summary>The session's tabs plus popups the engine attached but no action adopted yet, so closing leaves none behind.</summary>
    private static BrowserPageHandle[] WithUnadoptedPopups(BrowserSession session) =>
        session.Tabs.Concat(session.Tabs.SelectMany(page => session.Engine.TakeOpenedPages(page))).ToArray();

    /// <summary>Moves popups the session's pages opened into its tab list; past the limit they are closed.</summary>
    private void AdoptOpenedPages(BrowserSession session, JsonArray report)
    {
        foreach (var page in session.Tabs.ToArray())
        {
            foreach (var popup in session.Engine.TakeOpenedPages(page))
            {
                if (session.Tabs.Count >= BrowserLimits.MaxTabs)
                {
                    _ = ClosePageQuietlyAsync(session.Engine, popup);
                    report.Add(new JsonObject { ["closed"] = true, ["reason"] = "tab_limit" });
                    continue;
                }

                session.Tabs.Add(popup);
                report.Add(new JsonObject { ["tab"] = session.Tabs.Count });
            }
        }
    }

    private static async ValueTask<ToolExecutionOutcome> LockedAsync(
        BrowserSession session,
        Func<Task<ToolExecutionOutcome>> action,
        CancellationToken cancellationToken)
    {
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    private static async Task<JsonNode?> EvaluateJsonAsync(BrowserSession session, string expression, CancellationToken cancellationToken)
    {
        var json = await session.Engine.EvaluateAsync(RequirePage(session), expression, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return JsonValue.Create(json);
        }
    }

    private static Task NavigateCoreAsync(BrowserSession session, Uri url, CancellationToken cancellationToken) =>
        session.Engine.NavigateAsync(RequirePage(session), url.ToString(), cancellationToken);

    private static async Task<string> TitleAsync(BrowserSession session, CancellationToken cancellationToken)
    {
        try
        {
            return await session.Engine.TitleAsync(RequirePage(session), cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserEngineException)
        {
            return string.Empty;
        }
    }

    private static BrowserPageHandle RequirePage(BrowserSession session) =>
        session.Active >= 0 && session.Active < session.Tabs.Count
            ? session.Tabs[session.Active]
            : throw new BrowserEngineException("browser_not_open", "The page is closed; call action=open again.");

    private static async Task ClosePageQuietlyAsync(IBrowserEngine engine, BrowserPageHandle page)
    {
        try
        {
            await engine.ClosePageAsync(page, CancellationToken.None).ConfigureAwait(false);
        }
        catch (BrowserEngineException)
        {
            // The tab is already gone; closing is best effort.
        }
    }

    private static async Task CloseSessionAsync(BrowserSession session)
    {
        try
        {
            await session.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var tabs = WithUnadoptedPopups(session);
                session.Tabs.Clear();
                foreach (var page in tabs)
                {
                    await ClosePageQuietlyAsync(session.Engine, page).ConfigureAwait(false);
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

    private static ToolExecutionOutcome OpenOutput(BrowserSession session, Uri? start)
    {
        var output = new JsonObject
        {
            ["action"] = BrowserActions.Open,
            ["opened"] = true,
            ["url"] = start?.ToString() ?? "about:blank",
        };
        if (session.Persistent) output["profile"] = "persistent";
        return Success(output, "Browser opened.");
    }

    /// <summary>The width and height from a PNG header, or null when the bytes are not a PNG.</summary>
    private static (int Width, int Height)? PngSize(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G') return null;
        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return width > 0 && height > 0 ? (width, height) : null;
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

    private sealed class BrowserSession(IBrowserEngine engine, bool headless, bool persistent)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IBrowserEngine Engine { get; } = engine;

        public bool Headless { get; } = headless;

        public bool Persistent { get; } = persistent;

        /// <summary>The session's tabs in opening order; guarded by <see cref="Gate"/>.</summary>
        public List<BrowserPageHandle> Tabs { get; } = [];

        public int Active { get; set; } = -1;
    }
}
