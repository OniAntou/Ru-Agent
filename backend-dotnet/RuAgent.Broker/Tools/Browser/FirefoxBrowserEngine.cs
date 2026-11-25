using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools.Browser;

/// <summary>
/// The Firefox implementation of <see cref="IBrowserEngine"/> (ADR 0055): one browser process
/// driven over its native WebDriver BiDi endpoint, one browsing context per agent session. The
/// page snapshot, element refs and input scripts are the same JavaScript the Chromium engine
/// injects, so the Agent sees one contract regardless of the browser. Firefox has no CDP, so the
/// transport speaks BiDi JSON commands (<c>{id, method, params}</c>) instead of CDP JSON-RPC.
/// </summary>
public sealed class FirefoxBrowserEngine : IBrowserEngine
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private const int MaxBufferedEvents = 200;
    private const int MaxErrorChars = 512;
    private const int MaxConsoleTextChars = 2000;
    private const int MaxNetworkUrlChars = 2048;
    private const int MaxRunesPerAction = 256;
    private const int MaxRemoteValueDepth = 64;

    // Firefox 160 implements all three (verified against 160.0.1). They are subscribed one at a
    // time so a build without one domain still drives pages; the matching query then returns an
    // empty bounded list instead of failing the whole browser.
    private static readonly string[] SubscribedEvents =
    [
        "log.entryAdded",
        "network.beforeRequestSent",
        "network.responseCompleted",
        "browsingContext.userPromptOpened",
        "browsingContext.contextCreated",
    ];

    private static readonly IReadOnlyDictionary<string, string> KeyValues =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Enter"] = "\uE007",
            ["Tab"] = "\uE004",
            ["Escape"] = "\uE00C",
            ["Backspace"] = "\uE003",
            ["Delete"] = "\uE017",
            ["ArrowUp"] = "\uE013",
            ["ArrowDown"] = "\uE015",
            ["ArrowLeft"] = "\uE012",
            ["ArrowRight"] = "\uE014",
            ["Home"] = "\uE011",
            ["End"] = "\uE010",
            ["PageUp"] = "\uE00E",
            ["PageDown"] = "\uE00F",
            ["Insert"] = "\uE016",
            ["Space"] = "\uE00D",
        };

    private readonly string? _executable;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _pagesGate = new();
    private readonly Dictionary<string, PageState> _pagesByContext = new(StringComparer.Ordinal);
    private BidiConnection? _connection;
    private BidiBrowserProcess? _browser;
    private bool? _insertTextSupported;
    private int _disposed;

    private readonly bool _persistentProfile;
    private bool _runningHeadless = true;
    private int _pendingOpens;
    private string[] _startupContexts = [];

    /// <param name="persistentProfile">Run over Ru's persistent profile instead of an ephemeral one (ADR 0055 amendment 2026-11-25).</param>
    public FirefoxBrowserEngine(string? executable = null, bool persistentProfile = false)
    {
        _executable = executable;
        _persistentProfile = persistentProfile;
    }

    public bool IsAvailable => BrowserExecutableDiscovery.FindFirefoxExecutable(_executable) is not null;

    /// <summary>The installed Firefox path, or null when it is absent; used by tests and diagnostics.</summary>
    public static string? FindInstalledExecutable() => BrowserExecutableDiscovery.FindFirefoxExecutable();

    public async Task<BrowserPageHandle> OpenPageAsync(bool headless, CancellationToken cancellationToken)
    {
        // An open in flight keeps an idle visible browser from being shut down under it.
        Interlocked.Increment(ref _pendingOpens);
        try
        {
            return await OpenPageCoreAsync(headless, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingOpens);
        }
    }

    private async Task<BrowserPageHandle> OpenPageCoreAsync(bool headless, CancellationToken cancellationToken)
    {
        var connection = await EnsureStartedAsync(headless, cancellationToken).ConfigureAwait(false);
        JsonObject created;
        try
        {
            created = await connection.SendAsync(
                "browsingContext.create",
                new JsonObject { ["type"] = "tab" },
                RequestTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BidiRemoteException error)
        {
            throw new BrowserEngineException("browser_launch_failed", "The browser did not open a tab: " + Bound(Describe(error)));
        }

        var contextId = Required(created, "context");
        var page = new PageState(contextId);
        lock (_pagesGate)
        {
            _pagesByContext[contextId] = page;
        }

        await SetViewportAsync(connection, contextId, cancellationToken).ConfigureAwait(false);

        // The tab the browser opened at launch is closed once a page of ours exists, so a visible
        // browser never keeps a blank window that no session owns.
        foreach (var startup in Interlocked.Exchange(ref _startupContexts, []))
        {
            await CloseContextAsync(connection, startup, CancellationToken.None).ConfigureAwait(false);
        }

        return new BrowserPageHandle(contextId, contextId);
    }

    public async Task<byte[]> ScreenshotAsync(BrowserPageHandle pageHandle, string? elementRef, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var parameters = new JsonObject
        {
            ["context"] = page.ContextId,
            ["origin"] = "viewport",
            ["format"] = new JsonObject { ["type"] = "image/png" },
        };
        if (elementRef is not null)
        {
            var box = BrowserPageScripts.ParseBox(await EvaluateJsonAsync(
                page,
                BrowserPageScripts.ElementBox(BrowserPageScripts.RefIndex(elementRef)),
                "browser_eval_failed",
                cancellationToken).ConfigureAwait(false) as JsonObject);
            if (box is not { } clip)
            {
                throw new BrowserEngineException("browser_element_not_found", elementRef + " is not visible on the page; take a new snapshot.");
            }

            parameters["clip"] = new JsonObject
            {
                ["type"] = "box",
                ["x"] = clip.X,
                ["y"] = clip.Y,
                ["width"] = clip.Width,
                ["height"] = clip.Height,
            };
        }

        var result = await SendPageAsync(page, "browsingContext.captureScreenshot", parameters, "browser_screenshot_failed", RequestTimeout, cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = Convert.FromBase64String(TextOf(result, "data"));
            if (bytes.Length > 0) return bytes;
        }
        catch (FormatException)
        {
            // Fall through to the named failure below.
        }

        throw new BrowserEngineException("browser_screenshot_failed", "The browser did not return an image.");
    }

    public async Task HoverAsync(BrowserPageHandle pageHandle, string elementRef, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var point = BrowserPageScripts.ParsePoint(await EvaluateJsonAsync(
            page,
            BrowserPageScripts.ElementRect(BrowserPageScripts.RefIndex(elementRef)),
            "browser_eval_failed",
            cancellationToken).ConfigureAwait(false) as JsonObject);
        if (point is not { } target)
        {
            throw new BrowserEngineException("browser_element_not_found", elementRef + " is not on the page; take a new snapshot.");
        }

        await PerformActionsAsync(
            page,
            "pointer",
            new JsonArray(new JsonObject { ["type"] = "pointerMove", ["x"] = target.X, ["y"] = target.Y, ["origin"] = "viewport" }),
            cancellationToken).ConfigureAwait(false);
    }

    public void SetNextDialogAnswer(BrowserPageHandle pageHandle, bool accept, string? promptText) =>
        RequirePage(pageHandle).Dialogs.SetNextAnswer(accept, promptText);

    public IReadOnlyList<BrowserDialog> TakeDialogs(BrowserPageHandle pageHandle) =>
        TryPage(pageHandle) is { } page ? page.Dialogs.Take() : [];

    public IReadOnlyList<BrowserPageHandle> TakeOpenedPages(BrowserPageHandle pageHandle) =>
        TryPage(pageHandle) is { } page ? page.TakeOpened() : [];

    public async Task SetFilesAsync(
        BrowserPageHandle pageHandle,
        string elementRef,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var index = BrowserPageScripts.RefIndex(elementRef);
        var check = await EvaluateJsonAsync(page, BrowserPageScripts.FileInput(index), "browser_eval_failed", cancellationToken).ConfigureAwait(false);
        BrowserPageScripts.RequireFileInput(check as JsonObject, elementRef, paths.Count);
        var element = await EvaluateAsyncCore(page, BrowserPageScripts.RefElement(index), cancellationToken).ConfigureAwait(false);
        var sharedId = TextOf(element, "sharedId");
        if (TextOf(element, "type") != "node" || sharedId.Length == 0)
        {
            throw new BrowserEngineException("browser_element_not_found", elementRef + " is not on the page; take a new snapshot.");
        }

        var files = new JsonArray();
        foreach (var path in paths) files.Add(path);
        await SendPageAsync(
            page,
            "input.setFiles",
            new JsonObject
            {
                ["context"] = page.ContextId,
                ["element"] = new JsonObject { ["sharedId"] = sharedId },
                ["files"] = files,
            },
            "browser_upload_failed",
            RequestTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetViewportAsync(BidiConnection connection, string contextId, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(
                "browsingContext.setViewport",
                new JsonObject
                {
                    ["context"] = contextId,
                    ["viewport"] = new JsonObject
                    {
                        ["width"] = RuAgent.Protocol.BrowserLimits.MaxPageWidth,
                        ["height"] = RuAgent.Protocol.BrowserLimits.MaxPageHeight,
                    },
                },
                RequestTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BidiRemoteException)
        {
            // An older build keeps its window size; screenshots are then that size.
        }
    }

    private PageState? TryPage(BrowserPageHandle handle)
    {
        lock (_pagesGate)
        {
            return _pagesByContext.TryGetValue(handle.SessionId, out var page) && page.ContextId == handle.TargetId ? page : null;
        }
    }

    public async Task NavigateAsync(BrowserPageHandle pageHandle, string url, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        await SendPageAsync(
            page,
            "browsingContext.navigate",
            new JsonObject { ["context"] = page.ContextId, ["url"] = url, ["wait"] = "complete" },
            "browser_navigation_failed",
            NavigationTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrowserSnapshot> SnapshotAsync(
        BrowserPageHandle pageHandle,
        int maxElements,
        int maxTextChars,
        CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var payload = await EvaluateJsonAsync(
            page,
            BrowserPageScripts.Snapshot(maxElements, maxTextChars),
            "browser_snapshot_failed",
            cancellationToken).ConfigureAwait(false);
        if (payload is not JsonObject snapshot)
        {
            throw new BrowserEngineException("browser_snapshot_failed", "The page did not return a snapshot.");
        }

        return BrowserPageScripts.ParseSnapshot(snapshot, maxElements, maxTextChars);
    }

    public async Task ClickAsync(BrowserPageHandle pageHandle, string elementRef, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var index = BrowserPageScripts.RefIndex(elementRef);
        var point = BrowserPageScripts.ParsePoint(
            await EvaluateJsonAsync(page, BrowserPageScripts.ElementRect(index), "browser_eval_failed", cancellationToken).ConfigureAwait(false) as JsonObject);
        if (point is not { } target)
        {
            throw new BrowserEngineException("browser_element_not_found", elementRef + " is not on the page; take a new snapshot.");
        }

        var actions = new JsonArray(
            new JsonObject { ["type"] = "pointerMove", ["x"] = target.X, ["y"] = target.Y, ["origin"] = "viewport" },
            new JsonObject { ["type"] = "pointerDown", ["button"] = 0 },
            new JsonObject { ["type"] = "pointerUp", ["button"] = 0 });
        await PerformActionsAsync(page, "pointer", actions, cancellationToken).ConfigureAwait(false);
        // Releasing the pointer state keeps the next click independent of this dispatch path.
        await SendPageAsync(
            page,
            "input.releaseActions",
            new JsonObject { ["context"] = page.ContextId },
            "browser_eval_failed",
            RequestTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task TypeAsync(
        BrowserPageHandle pageHandle,
        string? elementRef,
        string text,
        bool replace,
        CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        if (elementRef is not null)
        {
            var index = BrowserPageScripts.RefIndex(elementRef);
            var focused = await EvaluateJsonAsync(page, BrowserPageScripts.Focus(index, replace), "browser_eval_failed", cancellationToken).ConfigureAwait(false);
            if (!BoolOf(focused))
            {
                throw new BrowserEngineException("browser_element_not_found", elementRef + " is not on the page; take a new snapshot.");
            }
        }

        if (text.Length == 0) return;

        // Firefox 160 answers input.insertText with "unknown command", so it is tried first and the
        // trusted keyboard path is the fallback (and the only path on this version).
        if (_insertTextSupported != false)
        {
            var connection = RequireConnection();
            try
            {
                await connection.SendAsync(
                    "input.insertText",
                    new JsonObject { ["context"] = page.ContextId, ["text"] = text },
                    RequestTimeout,
                    cancellationToken).ConfigureAwait(false);
                _insertTextSupported = true;
                return;
            }
            catch (BidiRemoteException error) when (error.ErrorCode == "unknown command")
            {
                _insertTextSupported = false;
            }
            catch (BidiRemoteException error) when (IsClosedContext(error))
            {
                throw NotOpen();
            }
            catch (BidiRemoteException error)
            {
                throw new BrowserEngineException("browser_eval_failed", Bound(Describe(error)));
            }
        }

        await TypeWithKeyActionsAsync(page, text, cancellationToken).ConfigureAwait(false);
    }

    public async Task FillAsync(BrowserPageHandle pageHandle, string elementRef, string text, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var index = BrowserPageScripts.RefIndex(elementRef);
        var filled = await EvaluateJsonAsync(page, BrowserPageScripts.Fill(index, text), "browser_eval_failed", cancellationToken).ConfigureAwait(false);
        if (!BoolOf(filled))
        {
            throw new BrowserEngineException(
                "browser_element_not_found",
                elementRef + " is not a fillable input, textarea or editable element on the page; take a new snapshot.");
        }
    }

    public async Task PressAsync(BrowserPageHandle pageHandle, string keyText, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        if (!KeyValues.TryGetValue(keyText, out var value))
        {
            throw new BrowserEngineException(
                "browser_key_invalid",
                "key must be one of " + string.Join(", ", KeyValues.Keys) + ".");
        }

        await PerformActionsAsync(page, "key", KeyActions([value]), cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> EvaluateAsync(BrowserPageHandle pageHandle, string expression, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var remote = await EvaluateAsyncCore(page, expression, cancellationToken).ConfigureAwait(false);
        return MapRemoteValue(remote)?.ToJsonString() ?? "null";
    }

    public async Task<string> TitleAsync(BrowserPageHandle pageHandle, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var remote = await EvaluateAsyncCore(page, "document.title", cancellationToken).ConfigureAwait(false);
        return TextOf(remote, "type") == "string" ? TextOf(remote, "value") : string.Empty;
    }

    public Task<IReadOnlyList<BrowserConsoleEntry>> ConsoleAsync(
        BrowserPageHandle pageHandle,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(RequirePage(pageHandle).RecentConsole(maxEntries));
    }

    public Task<IReadOnlyList<BrowserNetworkEntry>> NetworkAsync(
        BrowserPageHandle pageHandle,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(RequirePage(pageHandle).RecentNetwork(maxEntries));
    }

    public async Task ClosePageAsync(BrowserPageHandle pageHandle, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        RemovePage(page);
        var connection = _connection;
        if (connection is not null && !connection.IsClosed)
        {
            await CloseContextAsync(connection, page.ContextId, cancellationToken).ConfigureAwait(false);
        }

        await StopIfIdleAndVisibleAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A visible browser whose last page closed is shut down, so no window outlives the sessions
    /// that used it; a headless browser has no window and stays for the next open.
    /// </summary>
    private async Task StopIfIdleAndVisibleAsync()
    {
        if (_runningHeadless || Volatile.Read(ref _disposed) != 0) return;
        try
        {
            await _startGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_runningHeadless || Volatile.Read(ref _pendingOpens) > 0) return;
            lock (_pagesGate)
            {
                if (_pagesByContext.Count > 0) return;
            }

            await StopBrowserAsync().ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Closes the running browser cleanly and forgets it; the next open starts a new one. Holds <c>_startGate</c>.</summary>
    private async Task StopBrowserAsync()
    {
        if (_connection is { } connection)
        {
            connection.EventReceived -= OnBrowserEvent;
            await CloseCleanlyAsync(connection).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_browser is { } browser)
        {
            await browser.DisposeAsync().ConfigureAwait(false);
        }

        _connection = null;
        _browser = null;
        _startupContexts = [];
    }

    private static async Task<string[]> StartupContextsAsync(BidiConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var tree = await connection.SendAsync("browsingContext.getTree", new JsonObject { ["maxDepth"] = 0 }, RequestTimeout, cancellationToken).ConfigureAwait(false);
            return (tree["contexts"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(context => TextOf(context, "context"))
                .Where(id => id.Length > 0)
                .ToArray();
        }
        catch (BidiRemoteException)
        {
            // Without the list the startup tab stays; the browser still works.
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_pagesGate)
        {
            _pagesByContext.Clear();
        }

        if (_connection is { } connection)
        {
            connection.EventReceived -= OnBrowserEvent;
            await CloseCleanlyAsync(connection).ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }

        if (_browser is { } browser)
        {
            await browser.DisposeAsync().ConfigureAwait(false);
            _browser = null;
        }

        _startGate.Dispose();
    }

    private async Task<BidiConnection> EnsureStartedAsync(bool headless, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new BrowserEngineException("browser_closed", "The browser engine is closed; create a new one.");
        }

        if (_connection is { IsClosed: false } running && KeepsRunningBrowser(headless)) return running;

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new BrowserEngineException("browser_closed", "The browser engine is closed; create a new one.");
            }

            if (_connection is { IsClosed: false } runningNow && KeepsRunningBrowser(headless)) return runningNow;
            if (_browser is not null) await StopBrowserAsync().ConfigureAwait(false);

            _runningHeadless = headless;

            var executable = BrowserExecutableDiscovery.FindFirefoxExecutable(_executable)
                ?? throw new BrowserEngineException(
                    "browser_unavailable",
                    "No Firefox installation was found; install Firefox or set RU_BROWSER_EXE.");
            var browser = await BidiBrowserProcess.StartAsync(executable, headless, _persistentProfile, cancellationToken).ConfigureAwait(false);
            try
            {
                var connection = await BidiConnection.ConnectAsync(browser.WebSocketUri, cancellationToken).ConfigureAwait(false);
                try
                {
                    try
                    {
                        await NewSessionAsync(connection, cancellationToken).ConfigureAwait(false);
                    }
                    catch (BidiRemoteException error)
                    {
                        throw new BrowserEngineException(
                            "browser_launch_failed",
                            "The browser rejected the automation session: " + Bound(error.ErrorCode + (error.Detail.Length == 0 ? string.Empty : ": " + error.Detail)));
                    }

                    await SubscribeAsync(connection, cancellationToken).ConfigureAwait(false);
                    connection.EventReceived += OnBrowserEvent;
                    _browser = browser;
                    _connection = connection;
                    _startupContexts = await StartupContextsAsync(connection, cancellationToken).ConfigureAwait(false);
                    return connection;
                }
                catch (Exception)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception)
            {
                await browser.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// Starts the BiDi session so the engine answers dialogs itself ("ignore" leaves each prompt
    /// open for <c>browsingContext.handleUserPrompt</c>); a build that rejects the capability map
    /// falls back to the plain session, where Firefox applies its own default answer.
    /// </summary>
    private static async Task NewSessionAsync(BidiConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(
                "session.new",
                new JsonObject
                {
                    ["capabilities"] = new JsonObject
                    {
                        ["alwaysMatch"] = new JsonObject
                        {
                            ["unhandledPromptBehavior"] = new JsonObject { ["default"] = "ignore" },
                        },
                    },
                },
                RequestTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BidiRemoteException error) when (error.ErrorCode is "invalid argument" or "session not created")
        {
            await connection.SendAsync(
                "session.new",
                new JsonObject { ["capabilities"] = new JsonObject() },
                RequestTimeout,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The running browser serves a new page unless the page asks for a visible window while the
    /// browser runs headless and no page is open: then it restarts visibly, so a user can sign in
    /// to Ru's persistent profile (ADR 0055 amendment 2026-11-25).
    /// </summary>
    private bool KeepsRunningBrowser(bool headless)
    {
        if (headless || !_runningHeadless) return true;
        lock (_pagesGate)
        {
            return _pagesByContext.Count > 0;
        }
    }

    /// <summary>
    /// Asks the browser to exit on its own before its process is killed: a clean exit writes the
    /// persistent profile's cookies and lets Windows remove its windows, where a killed browser can
    /// leave unclosable ghost entries in Alt+Tab.
    /// </summary>
    private async Task CloseCleanlyAsync(BidiConnection connection)
    {
        if (connection.IsClosed) return;
        try
        {
            await connection.SendAsync("browser.close", new JsonObject(), TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is BrowserEngineException or BidiRemoteException)
        {
            // The browser is closing or already gone; the process wait still runs.
        }
    }

    private static async Task SubscribeAsync(BidiConnection connection, CancellationToken cancellationToken)
    {
        foreach (var name in SubscribedEvents)
        {
            try
            {
                await connection.SendAsync(
                    "session.subscribe",
                    new JsonObject { ["events"] = new JsonArray(name) },
                    RequestTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (BidiRemoteException)
            {
                // The domain is not implemented by this Firefox build: skip it and leave its buffer empty.
            }
        }
    }

    private async Task TypeWithKeyActionsAsync(PageState page, string text, CancellationToken cancellationToken)
    {
        var keyValues = new List<string>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            int codePoint;
            if (char.IsSurrogatePair(text, index))
            {
                codePoint = char.ConvertToUtf32(text, index);
                index++;
            }
            else
            {
                codePoint = text[index];
                if (char.IsSurrogate((char)codePoint)) continue;
            }

            if (codePoint is '\r' or '\n')
            {
                keyValues.Add(KeyValues["Enter"]);
                continue;
            }

            // Control characters have no printable key value; newlines are the only ones that
            // matter for ordinary typing, and fill/textarea paths carry exact text.
            if (codePoint < 0x20 || codePoint == 0x7F) continue;
            keyValues.Add(char.ConvertFromUtf32(codePoint));
        }

        for (var offset = 0; offset < keyValues.Count; offset += MaxRunesPerAction)
        {
            var count = Math.Min(MaxRunesPerAction, keyValues.Count - offset);
            await PerformActionsAsync(page, "key", KeyActions(keyValues.GetRange(offset, count)), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PerformActionsAsync(
        PageState page,
        string sourceType,
        JsonArray actions,
        CancellationToken cancellationToken)
    {
        var source = new JsonObject
        {
            ["type"] = sourceType,
            ["id"] = "ru-input",
            ["actions"] = actions,
        };
        if (sourceType == "pointer")
        {
            source["parameters"] = new JsonObject { ["pointerType"] = "mouse" };
        }

        await SendPageAsync(
            page,
            "input.performActions",
            new JsonObject { ["context"] = page.ContextId, ["actions"] = new JsonArray(source) },
            "browser_eval_failed",
            RequestTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static JsonArray KeyActions(IReadOnlyList<string> values)
    {
        var actions = new JsonArray();
        foreach (var value in values)
        {
            actions.Add(new JsonObject { ["type"] = "keyDown", ["value"] = value });
            actions.Add(new JsonObject { ["type"] = "keyUp", ["value"] = value });
        }

        return actions;
    }

    private async Task<JsonObject> SendPageAsync(
        PageState page,
        string method,
        JsonObject parameters,
        string failureCode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        try
        {
            return await connection.SendAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (BidiRemoteException error) when (IsClosedContext(error))
        {
            RemovePage(page);
            throw NotOpen();
        }
        catch (BidiRemoteException error)
        {
            throw new BrowserEngineException(failureCode, Bound(Describe(error)));
        }
    }

    private async Task<JsonObject?> EvaluateAsyncCore(PageState page, string expression, CancellationToken cancellationToken)
    {
        var result = await SendPageAsync(
            page,
            "script.evaluate",
            new JsonObject
            {
                ["expression"] = expression,
                ["target"] = new JsonObject { ["context"] = page.ContextId },
                ["awaitPromise"] = true,
                ["resultOwnership"] = "none",
            },
            "browser_eval_failed",
            RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (TextOf(result, "type") == "exception")
        {
            var details = result["exceptionDetails"] as JsonObject;
            var message = TextOf(details, "text");
            throw new BrowserEngineException(
                "browser_eval_failed",
                Bound(message.Length == 0 ? "The page script failed." : message));
        }

        return result["result"] as JsonObject;
    }

    private async Task<JsonNode?> EvaluateJsonAsync(
        PageState page,
        string expression,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var remote = await EvaluateAsyncCore(page, "JSON.stringify(" + expression + ")", cancellationToken).ConfigureAwait(false);
        if (TextOf(remote, "type") != "string")
        {
            throw new BrowserEngineException(failureCode, "The page did not return serializable data.");
        }

        var text = TextOf(remote, "value");
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new BrowserEngineException(failureCode, "The page returned data that is not JSON.");
        }
    }

    private BidiConnection RequireConnection() =>
        _connection is { IsClosed: false } connection
            ? connection
            : throw new BrowserEngineException("browser_closed", "The browser is not running; call action=open again.");

    private void OnBrowserEvent(string method, JsonObject? parameters)
    {
        if (parameters is null) return;
        if (method == "browsingContext.contextCreated")
        {
            OnContextCreated(parameters);
            return;
        }

        if (method == "browsingContext.userPromptOpened")
        {
            OnUserPrompt(parameters);
            return;
        }

        var contextId = method switch
        {
            "log.entryAdded" => TextOf(parameters["source"] as JsonObject, "context"),
            "network.beforeRequestSent" or "network.responseCompleted" => TextOf(parameters, "context"),
            _ => string.Empty,
        };
        if (contextId.Length == 0) return;
        PageState? page;
        lock (_pagesGate)
        {
            _pagesByContext.TryGetValue(contextId, out page);
        }

        page?.OnEvent(method, parameters);
    }

    private void OnUserPrompt(JsonObject parameters)
    {
        var contextId = TextOf(parameters, "context");
        PageState? page;
        lock (_pagesGate)
        {
            _pagesByContext.TryGetValue(contextId, out page);
        }

        if (page is null) return;
        var answer = page.Dialogs.Answer(TextOf(parameters, "type"), TextOf(parameters, "message"));
        // The receive loop must not wait on its own reply, so the answer is sent from a task.
        _ = AnswerPromptAsync(contextId, answer);
    }

    private async Task AnswerPromptAsync(string contextId, DialogAnswer answer)
    {
        var connection = _connection;
        if (connection is null || connection.IsClosed) return;
        try
        {
            var parameters = new JsonObject { ["context"] = contextId, ["accept"] = answer.Accept };
            if (answer.PromptText is not null) parameters["userText"] = answer.PromptText;
            await connection.SendAsync("browsingContext.handleUserPrompt", parameters, RequestTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is BrowserEngineException or BidiRemoteException)
        {
            // The prompt was already closed by the browser or the page went away.
        }
    }

    private void OnContextCreated(JsonObject parameters)
    {
        // Only a top-level context opened by one of the session pages becomes a tab.
        if (parameters["parent"] is JsonValue) return;
        var contextId = TextOf(parameters, "context");
        var openerId = TextOf(parameters, "originalOpener");
        if (contextId.Length == 0 || openerId.Length == 0) return;
        PageState? opener;
        var popup = new PageState(contextId);
        lock (_pagesGate)
        {
            if (!_pagesByContext.TryGetValue(openerId, out opener) || _pagesByContext.ContainsKey(contextId)) return;
            _pagesByContext[contextId] = popup;
        }

        opener.AddOpened(new BrowserPageHandle(contextId, contextId));
    }

    private PageState RequirePage(BrowserPageHandle handle)
    {
        lock (_pagesGate)
        {
            return _pagesByContext.TryGetValue(handle.SessionId, out var page) && page.ContextId == handle.TargetId
                ? page
                : throw new BrowserEngineException("browser_not_open", "The page is closed; call action=open again.");
        }
    }

    private void RemovePage(PageState page)
    {
        lock (_pagesGate)
        {
            _pagesByContext.Remove(page.ContextId);
        }
    }

    private static async Task CloseContextAsync(BidiConnection connection, string contextId, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(
                "browsingContext.close",
                new JsonObject { ["context"] = contextId },
                RequestTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is BrowserEngineException or BidiRemoteException)
        {
            // The context may already be gone; closing a page is best effort.
        }
    }

    private static JsonNode? MapRemoteValue(JsonObject? remoteValue, int depth = 0)
    {
        if (remoteValue is null || depth > MaxRemoteValueDepth) return null;
        switch (TextOf(remoteValue, "type"))
        {
            case "string":
                return JsonValue.Create(TextOf(remoteValue, "value"));
            case "boolean":
                return remoteValue["value"] is JsonValue boolean && boolean.TryGetValue<bool>(out var flag) ? JsonValue.Create(flag) : null;
            case "number":
                return MapNumber(remoteValue["value"]);
            case "bigint":
                // JSON.stringify throws for a bigint; a JSON string is the lossless representation.
                return JsonValue.Create(TextOf(remoteValue, "value"));
            case "date":
                // JSON.stringify of a Date is its quoted ISO string.
                return JsonValue.Create(TextOf(remoteValue, "value"));
            case "array":
                var array = new JsonArray();
                if (remoteValue["value"] is JsonArray items)
                {
                    foreach (var item in items) array.Add(MapRemoteValue(item as JsonObject, depth + 1));
                }

                return array;
            case "object":
                var mapped = new JsonObject();
                if (remoteValue["value"] is JsonArray pairs)
                {
                    foreach (var pair in pairs)
                    {
                        if (pair is not JsonArray { Count: 2 } entry) continue;
                        if (entry[0] is not JsonValue keyValue || !keyValue.TryGetValue<string>(out var key)) continue;
                        var property = entry[1] as JsonObject;
                        if (TextOf(property, "type") == "undefined") continue;
                        mapped[key] = MapRemoteValue(property, depth + 1);
                    }
                }

                return mapped;
            case "node" or "regexp" or "map" or "set" or "window":
                // JSON.stringify serializes these objects without their own data.
                return new JsonObject();
            default:
                // undefined, null, function, promise and other non-JSON values.
                return null;
        }
    }

    private static JsonNode? MapNumber(JsonNode? value)
    {
        if (value is not JsonValue number) return null;
        if (number.TryGetValue<double>(out var real))
        {
            return double.IsFinite(real) ? JsonValue.Create(real) : null;
        }

        // Firefox serializes NaN and the infinities as strings; JSON.stringify writes null for them.
        return null;
    }

    private static bool BoolOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static bool IsClosedContext(BidiRemoteException error) =>
        error.ErrorCode is "no such frame" or "no such navigable";

    private static BrowserEngineException NotOpen() =>
        new("browser_not_open", "The page is closed; call action=open again.");

    private static string Describe(BidiRemoteException error) =>
        error.Method + " failed: " + error.ErrorCode + (error.Detail.Length == 0 ? string.Empty : ": " + error.Detail);

    private static string Required(JsonObject source, string name) =>
        TextOf(source, name) is { Length: > 0 } value
            ? value
            : throw new BrowserEngineException("browser_launch_failed", "The browser did not return " + name + ".");

    private static string TextOf(JsonObject? source, string name) =>
        source?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static string Bound(string text) => text.Length <= MaxErrorChars ? text : text[..MaxErrorChars];

    private sealed class BidiRemoteException(string errorCode, string method, string detail) : Exception(detail)
    {
        public string ErrorCode { get; } = errorCode;

        public string Method { get; } = method;

        public string Detail { get; } = detail;
    }

    /// <summary>One WebDriver BiDi connection over a WebSocket: id-correlated commands plus events.</summary>
    private sealed class BidiConnection : IAsyncDisposable
    {
        private const int MaxMessageBytes = 32 * 1024 * 1024;
        private const int ReceiveBufferBytes = 64 * 1024;
        private readonly ClientWebSocket _socket;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _receiveLoop;
        private int _nextId;
        private int _closed;

        private BidiConnection(ClientWebSocket socket)
        {
            _socket = socket;
            _receiveLoop = Task.Run(ReceiveLoopAsync);
        }

        public event Action<string, JsonObject?>? EventReceived;

        public bool IsClosed => Volatile.Read(ref _closed) != 0;

        public static async Task<BidiConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            try
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is WebSocketException or HttpRequestException or ArgumentException)
            {
                socket.Dispose();
                throw new BrowserEngineException("browser_launch_failed", "The browser WebDriver BiDi connection failed: " + Bound(error.Message));
            }

            return new BidiConnection(socket);
        }

        public async Task<JsonObject> SendAsync(
            string method,
            JsonObject? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (IsClosed) throw new BrowserEngineException("browser_closed", "The browser connection is closed.");
            var id = Interlocked.Increment(ref _nextId);
            var message = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters is not null) message["params"] = parameters.DeepClone();
            var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
            {
                throw new BrowserEngineException("browser_closed", "A duplicate BiDi request id was generated.");
            }

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            attempt.CancelAfter(timeout);
            try
            {
                var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
                await _sendGate.WaitAsync(attempt.Token).ConfigureAwait(false);
                try
                {
                    await _socket.SendAsync(payload, WebSocketMessageType.Text, true, attempt.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendGate.Release();
                }

                using var registration = attempt.Token.Register(
                    static state => ((TaskCompletionSource<JsonObject>)state!).TrySetCanceled(),
                    completion);
                var response = await completion.Task.ConfigureAwait(false);
                if (TextOf(response, "type") == "error")
                {
                    throw new BidiRemoteException(TextOf(response, "error"), method, TextOf(response, "message"));
                }

                return response["result"] as JsonObject ?? [];
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new BrowserEngineException(
                    IsClosed ? "browser_closed" : "browser_timeout",
                    IsClosed ? "The browser closed during the action; open it again." : method + " did not answer in time.");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Close();
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The receive loop already reports its own failure through the closed state.
            }

            _socket.Dispose();
            _sendGate.Dispose();
            _lifetime.Dispose();
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[ReceiveBufferBytes];
            using var message = new MemoryStream();
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }

                        message.Write(buffer, 0, result.Count);
                        if (message.Length > MaxMessageBytes)
                        {
                            return;
                        }
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        Dispatch(message.ToArray());
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal shutdown path.
            }
            catch (Exception error) when (error is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                // A broken socket is reported to every waiter by Close below.
            }
            finally
            {
                Close();
            }
        }

        private void Dispatch(byte[] payload)
        {
            JsonObject? message;
            try
            {
                message = JsonNode.Parse(payload) as JsonObject;
            }
            catch (JsonException)
            {
                return;
            }

            if (message is null) return;
            if (message["id"] is JsonValue idValue
                && idValue.TryGetValue<int>(out var id)
                && _pending.TryGetValue(id, out var completion))
            {
                completion.TrySetResult(message);
                return;
            }

            if (TextOf(message, "type") != "event") return;
            var method = TextOf(message, "method");
            if (method.Length == 0) return;
            try
            {
                EventReceived?.Invoke(method, message["params"] as JsonObject);
            }
            catch (Exception)
            {
                // A subscriber fault must not stop the receive loop.
            }
        }

        private void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try
            {
                _socket.Abort();
            }
            catch (Exception)
            {
                // Abort is best effort; the cancellation below reports the closure.
            }

            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposal already cancelled the lifetime.
            }

            var closed = new BrowserEngineException(
                "browser_closed",
                "The browser connection closed unexpectedly; open the browser again.");
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(closed);
            }

            _pending.Clear();
        }

        private static string TextOf(JsonObject? source, string name) =>
            source?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

        private static string Bound(string text) => text.Length <= MaxErrorChars ? text : text[..MaxErrorChars];
    }

    /// <summary>
    /// A Firefox process started for the tool with a random WebDriver BiDi port (ADR 0055). Disposal
    /// kills the process tree and releases the profile: an ephemeral profile is deleted, so no cookie
    /// survives the run; Ru's persistent profile (amendment 2026-11-25) is kept.
    /// </summary>
    private sealed class BidiBrowserProcess : IAsyncDisposable
    {
        private const string ServerFileName = "WebDriverBiDiServer.json";
        private const int MaxErrorChars = 256;
        private readonly Process _process;
        private readonly BrowserProfileLease _profile;
        private readonly WindowsJobProcessTree? _job;
        private int _disposed;

        private BidiBrowserProcess(Process process, BrowserProfileLease profile, Uri webSocketUri, WindowsJobProcessTree? job)
        {
            _process = process;
            _profile = profile;
            _job = job;
            WebSocketUri = webSocketUri;
        }

        public Uri WebSocketUri { get; }

        public static async Task<BidiBrowserProcess> StartAsync(
            string executable,
            bool headless,
            bool persistentProfile,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executable);
            var profile = persistentProfile
                ? BrowserProfileLease.Persistent("firefox", [ServerFileName])
                : BrowserProfileLease.Ephemeral();

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = BrowserProfileDirectory.Root(),
            };
            if (headless) startInfo.ArgumentList.Add("-headless");
            startInfo.ArgumentList.Add("-no-remote");
            startInfo.ArgumentList.Add("--remote-debugging-port");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("-profile");
            startInfo.ArgumentList.Add(profile.Directory);
            startInfo.ArgumentList.Add("about:blank");

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                await profile.DisposeAsync().ConfigureAwait(false);
                throw new BrowserEngineException("browser_launch_failed", "The browser could not start: " + Bound(error.Message));
            }

            if (process is null)
            {
                await profile.DisposeAsync().ConfigureAwait(false);
                throw new BrowserEngineException("browser_launch_failed", "The browser process did not start.");
            }

            // Draining both pipes keeps a chatty browser from blocking on a full pipe.
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var job = BrowserProcessJob.TryAttach(process);
            try
            {
                var webSocketUri = await WaitForServerAsync(process, profile.Directory, cancellationToken).ConfigureAwait(false);
                return new BidiBrowserProcess(process, profile, webSocketUri, job);
            }
            catch (Exception)
            {
                BrowserProcessJob.Release(job);
                Kill(process);
                await profile.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // The engine asked the browser to close; letting it exit writes a persistent profile's
            // cookies and removes its windows cleanly, which a kill could not. Firefox runs
            // through a launcher, so the job's process count decides when it is gone.
            await BrowserProcessJob.WaitForExitAsync(_job, _process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);

            BrowserProcessJob.Release(_job);
            Kill(_process);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
            {
                // The process tree was already killed; a stuck exit handle must not fail disposal.
            }

            _process.Dispose();
            await _profile.DisposeAsync().ConfigureAwait(false);
        }

        private static async Task<Uri> WaitForServerAsync(Process process, string profile, CancellationToken cancellationToken)
        {
            // Firefox writes the actual port after it starts listening; the probe verified the
            // shipped 160.0.1 build writes {"ws_host": "...", "ws_port": <port>}.
            var serverFile = Path.Combine(profile, ServerFileName);
            var deadline = DateTime.UtcNow + StartupTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new BrowserEngineException(
                        "browser_launch_failed",
                        "The browser exited during startup with code " + process.ExitCode + ".");
                }

                if (File.Exists(serverFile))
                {
                    string? json = null;
                    try
                    {
                        json = await File.ReadAllTextAsync(serverFile, cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // The browser may still be writing the file; the next poll retries.
                    }

                    if (TryReadWebSocketUri(json) is { } webSocketUri) return webSocketUri;
                }

                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }

            throw new BrowserEngineException(
                "browser_launch_failed",
                "The browser did not open its WebDriver BiDi port within " + StartupTimeout.TotalSeconds + " seconds.");
        }

        private static Uri? TryReadWebSocketUri(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                if (JsonNode.Parse(json) is not JsonObject server) return null;
                if (server["webSocketUrl"] is JsonValue urlValue
                    && urlValue.TryGetValue<string>(out var url)
                    && Uri.TryCreate(url, UriKind.Absolute, out var advertised)
                    && advertised.Scheme == "ws")
                {
                    return advertised;
                }

                if (server["ws_host"] is JsonValue hostValue
                    && hostValue.TryGetValue<string>(out var host)
                    && !string.IsNullOrWhiteSpace(host)
                    && server["ws_port"] is JsonValue portValue
                    && portValue.TryGetValue<int>(out var port)
                    && port is > 0 and <= 65535)
                {
                    return new UriBuilder("ws", host, port, "/session").Uri;
                }

                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Best effort: the profile cleanup below still runs.
            }
        }

        private static string Bound(string text) => text.Length <= MaxErrorChars ? text : text[..MaxErrorChars];
    }

    /// <summary>Bounded console and network buffers for one browsing context.</summary>
    private sealed class PageState(string contextId)
    {
        private readonly object _gate = new();
        private readonly LinkedList<BrowserConsoleEntry> _console = new();
        private readonly List<BrowserNetworkEntry> _network = [];
        private readonly List<string> _networkIds = [];
        private readonly Dictionary<string, int> _networkIndex = new(StringComparer.Ordinal);

        public string ContextId { get; } = contextId;

        public BrowserDialogBook Dialogs { get; } = new();

        private readonly List<BrowserPageHandle> _opened = [];

        public void AddOpened(BrowserPageHandle popup)
        {
            lock (_gate) _opened.Add(popup);
        }

        public IReadOnlyList<BrowserPageHandle> TakeOpened()
        {
            lock (_gate)
            {
                if (_opened.Count == 0) return [];
                var taken = _opened.ToArray();
                _opened.Clear();
                return taken;
            }
        }

        public void OnEvent(string method, JsonObject parameters)
        {
            switch (method)
            {
                case "log.entryAdded":
                    AddLogEntry(parameters);
                    break;
                case "network.beforeRequestSent":
                    AddRequest(parameters);
                    break;
                case "network.responseCompleted":
                    UpdateResponse(parameters);
                    break;
                default:
                    break;
            }
        }

        public IReadOnlyList<BrowserConsoleEntry> RecentConsole(int max)
        {
            lock (_gate)
            {
                return _console.Skip(Math.Max(0, _console.Count - max)).ToArray();
            }
        }

        public IReadOnlyList<BrowserNetworkEntry> RecentNetwork(int max)
        {
            lock (_gate)
            {
                return _network.Skip(Math.Max(0, _network.Count - max)).ToArray();
            }
        }

        private void AddLogEntry(JsonObject parameters)
        {
            var text = TextOf(parameters, "text");
            if (text.Length == 0 && parameters["args"] is JsonArray args)
            {
                var parts = new List<string>();
                foreach (var item in args)
                {
                    var value = item as JsonObject;
                    if (TextOf(value, "type") == "string") parts.Add(TextOf(value, "value"));
                }

                text = string.Join(' ', parts);
            }

            if (text.Length > MaxConsoleTextChars) text = text[..MaxConsoleTextChars];
            var level = TextOf(parameters, "level");
            lock (_gate)
            {
                _console.AddLast(new BrowserConsoleEntry(level.Length == 0 ? "log" : level, text));
                while (_console.Count > MaxBufferedEvents) _console.RemoveFirst();
            }
        }

        private void AddRequest(JsonObject parameters)
        {
            if (parameters["request"] is not JsonObject request) return;
            AppendNetwork(TextOf(request, "request"), RequestEntry(request, null));
        }

        private void UpdateResponse(JsonObject parameters)
        {
            if (parameters["request"] is not JsonObject request) return;
            var status = parameters["response"] is JsonObject response
                && response["status"] is JsonValue statusValue
                && statusValue.TryGetValue<int>(out var code)
                    ? code
                    : (int?)null;
            var requestId = TextOf(request, "request");
            if (requestId.Length == 0) return;
            lock (_gate)
            {
                if (_networkIndex.TryGetValue(requestId, out var index))
                {
                    _network[index] = _network[index] with { Status = status };
                    return;
                }
            }

            // A response can be reported without its request when the subscription started late.
            AppendNetwork(requestId, RequestEntry(request, status));
        }

        private static BrowserNetworkEntry RequestEntry(JsonObject request, int? status) =>
            new(
                TextOf(request, "method"),
                BoundUrl(TextOf(request, "url")),
                status,
                ResourceTypeOf(request));

        private static string ResourceTypeOf(JsonObject request)
        {
            var destination = TextOf(request, "destination");
            if (destination.Length > 0) return destination;
            var initiatorType = TextOf(request, "initiatorType");
            return initiatorType.Length > 0 ? initiatorType : "other";
        }

        private void AppendNetwork(string requestId, BrowserNetworkEntry entry)
        {
            if (requestId.Length == 0) return;
            lock (_gate)
            {
                if (_networkIndex.TryGetValue(requestId, out var existing))
                {
                    _network[existing] = entry;
                    return;
                }

                if (_network.Count >= MaxBufferedEvents)
                {
                    _network.RemoveAt(0);
                    _networkIds.RemoveAt(0);
                    _networkIndex.Clear();
                    for (var index = 0; index < _networkIds.Count; index++)
                    {
                        _networkIndex[_networkIds[index]] = index;
                    }
                }

                _network.Add(entry);
                _networkIds.Add(requestId);
                _networkIndex[requestId] = _network.Count - 1;
            }
        }

        private static string BoundUrl(string url) => url.Length <= MaxNetworkUrlChars ? url : url[..MaxNetworkUrlChars];

        private static string TextOf(JsonObject? source, string name) =>
            source?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
    }
}
