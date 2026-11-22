using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Browser;

/// <summary>
/// The Chromium implementation of <see cref="IBrowserEngine"/> (ADR 0055): one browser process
/// per engine, one flattened CDP page session per agent session. The page snapshot is a
/// visible-DOM view with stable element refs; input is sent through the CDP Input domain so a
/// click is a real trusted pointer event, not a synthetic DOM call.
/// </summary>
public sealed class ChromiumBrowserEngine : IBrowserEngine
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private const int MaxBufferedEvents = 200;
    private const int MaxErrorChars = 512;
    private const int MaxConsoleTextChars = 2000;

    private readonly string? _executable;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _pagesGate = new();
    private readonly Dictionary<string, PageState> _pagesBySession = new(StringComparer.Ordinal);
    private CdpConnection? _connection;
    private CdpBrowserProcess? _browser;
    private int _disposed;

    public ChromiumBrowserEngine(string? executable = null) => _executable = executable;

    public bool IsAvailable => BrowserExecutableDiscovery.FindChromiumExecutable(_executable) is not null;

    /// <summary>The installed Chromium-family path, or null when none is present; used by tests and diagnostics.</summary>
    public static string? FindInstalledExecutable() => BrowserExecutableDiscovery.FindChromiumExecutable();

    public async Task<BrowserPageHandle> OpenPageAsync(bool headless, CancellationToken cancellationToken)
    {
        var connection = await EnsureStartedAsync(headless, cancellationToken).ConfigureAwait(false);
        var created = await connection.SendAsync(
            "Target.createTarget",
            new JsonObject { ["url"] = "about:blank" },
            sessionId: null,
            RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var targetId = Required(created, "targetId");
        var attached = await connection.SendAsync(
            "Target.attachToTarget",
            new JsonObject { ["targetId"] = targetId, ["flatten"] = true },
            sessionId: null,
            RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        var sessionId = Required(attached, "sessionId");
        var page = new PageState(targetId, sessionId);
        lock (_pagesGate)
        {
            _pagesBySession[sessionId] = page;
        }

        try
        {
            foreach (var domain in new[] { "Page", "Runtime", "Log", "Network" })
            {
                await connection.SendAsync(domain + ".enable", new JsonObject(), sessionId, RequestTimeout, cancellationToken).ConfigureAwait(false);
            }

            await connection.SendAsync(
                "Emulation.setDeviceMetricsOverride",
                new JsonObject
                {
                    ["width"] = RuAgent.Protocol.BrowserLimits.MaxPageWidth,
                    ["height"] = RuAgent.Protocol.BrowserLimits.MaxPageHeight,
                    ["deviceScaleFactor"] = 1,
                    ["mobile"] = false,
                },
                sessionId,
                RequestTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            RemovePage(page);
            throw;
        }

        return new BrowserPageHandle(targetId, sessionId);
    }

    public async Task NavigateAsync(BrowserPageHandle pageHandle, string url, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var result = await SendAsync(page, "Page.navigate", new JsonObject { ["url"] = url }, cancellationToken).ConfigureAwait(false);
        var errorText = TextOf(result, "errorText");
        if (errorText.Length > 0)
        {
            throw new BrowserEngineException("browser_navigation_failed", "The page could not be loaded: " + Bound(errorText));
        }

        await WaitForReadyAsync(page, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrowserSnapshot> SnapshotAsync(
        BrowserPageHandle pageHandle,
        int maxElements,
        int maxTextChars,
        CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var script = BrowserPageScripts.Snapshot(maxElements, maxTextChars);
        var value = await SendEvaluateAsync(page, script, cancellationToken).ConfigureAwait(false);
        if (value["value"] is not JsonObject payload)
        {
            throw new BrowserEngineException("browser_snapshot_failed", "The page did not return a snapshot.");
        }

        return BrowserPageScripts.ParseSnapshot(payload, maxElements, maxTextChars);
    }

    public async Task ClickAsync(BrowserPageHandle pageHandle, string elementRef, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var index = BrowserPageScripts.RefIndex(elementRef);
        var value = await SendEvaluateAsync(page, BrowserPageScripts.ElementRect(index), cancellationToken).ConfigureAwait(false);
        if (BrowserPageScripts.ParsePoint(value["value"] as JsonObject) is not { } point)
        {
            throw new BrowserEngineException("browser_element_not_found", elementRef + " is not on the page; take a new snapshot.");
        }

        var x = point.X;
        var y = point.Y;

        await SendAsync(page, "Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y }, cancellationToken).ConfigureAwait(false);
        await SendAsync(
            page,
            "Input.dispatchMouseEvent",
            new JsonObject
            {
                ["type"] = "mousePressed",
                ["x"] = x,
                ["y"] = y,
                ["button"] = "left",
                ["buttons"] = 1,
                ["clickCount"] = 1,
            },
            cancellationToken).ConfigureAwait(false);
        await SendAsync(
            page,
            "Input.dispatchMouseEvent",
            new JsonObject
            {
                ["type"] = "mouseReleased",
                ["x"] = x,
                ["y"] = y,
                ["button"] = "left",
                ["buttons"] = 0,
                ["clickCount"] = 1,
            },
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
            var focused = await SendEvaluateAsync(page, BrowserPageScripts.Focus(index, replace), cancellationToken).ConfigureAwait(false);
            if (!BrowserPageScripts.BoolOf(focused, "value"))
            {
                throw new BrowserEngineException("browser_element_not_found", elementRef + " is not on the page; take a new snapshot.");
            }
        }

        await SendAsync(page, "Input.insertText", new JsonObject { ["text"] = text }, cancellationToken).ConfigureAwait(false);
    }

    public async Task FillAsync(BrowserPageHandle pageHandle, string elementRef, string text, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var index = BrowserPageScripts.RefIndex(elementRef);
        var filled = await SendEvaluateAsync(page, BrowserPageScripts.Fill(index, text), cancellationToken).ConfigureAwait(false);
        if (!BrowserPageScripts.BoolOf(filled, "value"))
        {
            throw new BrowserEngineException(
                "browser_element_not_found",
                elementRef + " is not a fillable input, textarea or editable element on the page; take a new snapshot.");
        }
    }

    public async Task PressAsync(BrowserPageHandle pageHandle, string keyText, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        if (!Keys.TryGetValue(keyText, out var key))
        {
            throw new BrowserEngineException(
                "browser_key_invalid",
                "key must be one of " + string.Join(", ", Keys.Keys) + ".");
        }

        var down = new JsonObject
        {
            ["type"] = key.Text is null ? "rawKeyDown" : "keyDown",
            ["key"] = key.Key,
            ["code"] = key.Code,
            ["windowsVirtualKeyCode"] = key.VirtualKey,
            ["nativeVirtualKeyCode"] = key.VirtualKey,
        };
        if (key.Text is not null) down["text"] = key.Text;
        await SendAsync(page, "Input.dispatchKeyEvent", down, cancellationToken).ConfigureAwait(false);
        await SendAsync(
            page,
            "Input.dispatchKeyEvent",
            new JsonObject
            {
                ["type"] = "keyUp",
                ["key"] = key.Key,
                ["code"] = key.Code,
                ["windowsVirtualKeyCode"] = key.VirtualKey,
                ["nativeVirtualKeyCode"] = key.VirtualKey,
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> EvaluateAsync(BrowserPageHandle pageHandle, string expression, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var value = await SendEvaluateAsync(page, expression, cancellationToken).ConfigureAwait(false);
        return value["value"] is { } node ? node.ToJsonString() : "null";
    }

    public async Task<string> TitleAsync(BrowserPageHandle pageHandle, CancellationToken cancellationToken)
    {
        var page = RequirePage(pageHandle);
        var value = await SendEvaluateAsync(page, "document.title", cancellationToken).ConfigureAwait(false);
        return TextOf(value, "value");
    }

    public Task<IReadOnlyList<BrowserConsoleEntry>> ConsoleAsync(
        BrowserPageHandle pageHandle,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var page = RequirePage(pageHandle);
        return Task.FromResult(page.RecentConsole(maxEntries));
    }

    public Task<IReadOnlyList<BrowserNetworkEntry>> NetworkAsync(
        BrowserPageHandle pageHandle,
        int maxEntries,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var page = RequirePage(pageHandle);
        return Task.FromResult(page.RecentNetwork(maxEntries));
    }

    public Task ClosePageAsync(BrowserPageHandle pageHandle, CancellationToken cancellationToken)
    {
        var connection = _connection;
        var page = RequirePage(pageHandle);
        RemovePage(page);
        return connection is null || connection.IsClosed
            ? Task.CompletedTask
            : CloseTargetAsync(connection, pageHandle.TargetId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_pagesGate)
        {
            _pagesBySession.Clear();
        }

        if (_connection is { } connection)
        {
            connection.EventReceived -= OnBrowserEvent;
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

    private async Task<CdpConnection> EnsureStartedAsync(bool headless, CancellationToken cancellationToken)
    {
        if (_connection is { IsClosed: false } running) return running;
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsClosed: false } runningNow) return runningNow;
            if (_browser is { } stale)
            {
                if (_connection is { } staleConnection)
                {
                    staleConnection.EventReceived -= OnBrowserEvent;
                    await staleConnection.DisposeAsync().ConfigureAwait(false);
                }

                await stale.DisposeAsync().ConfigureAwait(false);
                _connection = null;
                _browser = null;
            }

            var executable = BrowserExecutableDiscovery.FindChromiumExecutable(_executable)
                ?? throw new BrowserEngineException(
                    "browser_unavailable",
                    "No Chromium browser (Edge, Chrome, Brave, Vivaldi, Opera, Chromium or Arc) was found; install one or set RU_BROWSER_EXE.");
            var browser = await CdpBrowserProcess.StartAsync(executable, headless, cancellationToken).ConfigureAwait(false);
            try
            {
                var connection = await CdpConnection.ConnectAsync(browser.WebSocketUri, cancellationToken).ConfigureAwait(false);
                connection.EventReceived += OnBrowserEvent;
                _browser = browser;
                _connection = connection;
                return connection;
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

    private async Task WaitForReadyAsync(PageState page, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + NavigationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = await SendEvaluateAsync(page, "document.readyState", cancellationToken).ConfigureAwait(false);
                if (TextOf(value, "value") is "complete") return;
            }
            catch (BrowserEngineException error) when (error.Code is not "browser_timeout" and not "browser_closed")
            {
                // A navigation replaces the execution context; the next poll reads the new one.
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new BrowserEngineException("browser_timeout", "The page did not finish loading in time.");
    }

    private Task<JsonObject> SendAsync(PageState page, string method, JsonObject? parameters, CancellationToken cancellationToken) =>
        SendCoreAsync(page.SessionId, method, parameters, cancellationToken);

    private Task<JsonObject> SendCoreAsync(string sessionId, string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new BrowserEngineException("browser_closed", "The browser is not running; call action=open again.");
        return connection.SendAsync(method, parameters, sessionId, RequestTimeout, cancellationToken);
    }

    private async Task<JsonObject> SendEvaluateAsync(PageState page, string expression, CancellationToken cancellationToken)
    {
        var result = await SendAsync(
            page,
            "Runtime.evaluate",
            new JsonObject
            {
                ["expression"] = expression,
                ["returnByValue"] = true,
                ["awaitPromise"] = true,
                ["userGesture"] = true,
            },
            cancellationToken).ConfigureAwait(false);
        if (result["exceptionDetails"] is JsonObject exception)
        {
            var description = TextOf(exception, "text");
            if (exception["exception"] is JsonObject inner && TextOf(inner, "description") is { Length: > 0 } detail)
            {
                description = detail;
            }

            throw new BrowserEngineException("browser_eval_failed", Bound(description.Length == 0 ? "The page script failed." : description));
        }

        return result["result"] as JsonObject ?? [];
    }

    private void OnBrowserEvent(string method, string? sessionId, JsonObject? parameters)
    {
        if (sessionId is null || parameters is null) return;
        PageState? page;
        lock (_pagesGate)
        {
            _pagesBySession.TryGetValue(sessionId, out page);
        }

        page?.OnEvent(method, parameters);
    }

    private PageState RequirePage(BrowserPageHandle handle)
    {
        lock (_pagesGate)
        {
            return _pagesBySession.TryGetValue(handle.SessionId, out var page) && page.TargetId == handle.TargetId
                ? page
                : throw new BrowserEngineException("browser_not_open", "The page is closed; call action=open again.");
        }
    }

    private void RemovePage(PageState page)
    {
        lock (_pagesGate)
        {
            _pagesBySession.Remove(page.SessionId);
        }
    }

    private static async Task CloseTargetAsync(CdpConnection connection, string targetId, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(
                "Target.closeTarget",
                new JsonObject { ["targetId"] = targetId },
                sessionId: null,
                RequestTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (BrowserEngineException)
        {
            // The target may already be gone; closing a page is best effort.
        }
    }

    private static string Required(JsonObject source, string name) =>
        TextOf(source, name) is { Length: > 0 } value
            ? value
            : throw new BrowserEngineException("browser_launch_failed", "The browser did not return " + name + ".");

    private static string TextOf(JsonObject? source, string name) =>
        source?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static string Bound(string text) => text.Length <= MaxErrorChars ? text : text[..MaxErrorChars];

    private static readonly IReadOnlyDictionary<string, KeyDefinition> Keys =
        new Dictionary<string, KeyDefinition>(StringComparer.Ordinal)
        {
            ["Enter"] = new("Enter", "Enter", 13, "\r"),
            ["Tab"] = new("Tab", "Tab", 9, null),
            ["Escape"] = new("Escape", "Escape", 27, null),
            ["Backspace"] = new("Backspace", "Backspace", 8, null),
            ["Delete"] = new("Delete", "Delete", 46, null),
            ["ArrowUp"] = new("ArrowUp", "ArrowUp", 38, null),
            ["ArrowDown"] = new("ArrowDown", "ArrowDown", 40, null),
            ["ArrowLeft"] = new("ArrowLeft", "ArrowLeft", 37, null),
            ["ArrowRight"] = new("ArrowRight", "ArrowRight", 39, null),
            ["Home"] = new("Home", "Home", 36, null),
            ["End"] = new("End", "End", 35, null),
            ["PageUp"] = new("PageUp", "PageUp", 33, null),
            ["PageDown"] = new("PageDown", "PageDown", 34, null),
            ["Insert"] = new("Insert", "Insert", 45, null),
            ["Space"] = new(" ", "Space", 32, " "),
        };

    private sealed record KeyDefinition(string Key, string Code, int VirtualKey, string? Text);

    private sealed class PageState(string targetId, string sessionId)
    {
        private readonly object _gate = new();
        private readonly LinkedList<BrowserConsoleEntry> _console = new();
        private readonly List<BrowserNetworkEntry> _network = [];
        private readonly List<string> _networkIds = [];
        private readonly Dictionary<string, int> _networkIndex = new(StringComparer.Ordinal);

        public string TargetId { get; } = targetId;

        public string SessionId { get; } = sessionId;

        public void OnEvent(string method, JsonObject parameters)
        {
            switch (method)
            {
                case "Runtime.consoleAPICalled":
                    AddConsole(parameters);
                    break;
                case "Log.entryAdded":
                    AddLogEntry(parameters);
                    break;
                case "Network.requestWillBeSent":
                    AddRequest(parameters);
                    break;
                case "Network.responseReceived":
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

        private void AddConsole(JsonObject parameters)
        {
            var level = TextOf(parameters, "type");
            var parts = new List<string>();
            if (parameters["args"] is JsonArray args)
            {
                foreach (var item in args)
                {
                    if (item is not JsonObject argument) continue;
                    if (argument["value"] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        parts.Add(text);
                    }
                    else if (argument["value"] is { } other)
                    {
                        parts.Add(other.ToJsonString());
                    }
                    else if (TextOf(argument, "description") is { Length: > 0 } description)
                    {
                        parts.Add(description);
                    }
                }
            }

            var message = string.Join(' ', parts);
            if (message.Length > MaxConsoleTextChars) message = message[..MaxConsoleTextChars];
            lock (_gate)
            {
                _console.AddLast(new BrowserConsoleEntry(level.Length == 0 ? "log" : level, message));
                while (_console.Count > MaxBufferedEvents) _console.RemoveFirst();
            }
        }

        private void AddLogEntry(JsonObject parameters)
        {
            if (parameters["entry"] is not JsonObject entry) return;
            var text = TextOf(entry, "text");
            if (text.Length > MaxConsoleTextChars) text = text[..MaxConsoleTextChars];
            var level = TextOf(entry, "level");
            lock (_gate)
            {
                _console.AddLast(new BrowserConsoleEntry(level.Length == 0 ? "log" : level, text));
                while (_console.Count > MaxBufferedEvents) _console.RemoveFirst();
            }
        }

        private void AddRequest(JsonObject parameters)
        {
            if (parameters["request"] is not JsonObject request) return;
            var requestId = TextOf(parameters, "requestId");
            if (requestId.Length == 0) return;
            var entry = new BrowserNetworkEntry(
                TextOf(request, "method"),
                BoundUrl(TextOf(request, "url")),
                null,
                TextOf(parameters, "type"));
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

        private void UpdateResponse(JsonObject parameters)
        {
            if (parameters["response"] is not JsonObject response) return;
            var requestId = TextOf(parameters, "requestId");
            var status = response["status"] is JsonValue value && value.TryGetValue<int>(out var code) ? code : (int?)null;
            lock (_gate)
            {
                if (!_networkIndex.TryGetValue(requestId, out var index)) return;
                var previous = _network[index];
                _network[index] = previous with { Status = status };
            }
        }

        private static string BoundUrl(string url) => url.Length <= 2048 ? url : url[..2048];

        private static string TextOf(JsonObject? source, string name) =>
            source?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
    }

}
