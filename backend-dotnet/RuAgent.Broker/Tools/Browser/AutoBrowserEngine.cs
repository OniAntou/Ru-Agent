namespace RuAgent.Broker.Tools.Browser;

/// <summary>
/// Chooses the browser family once, at the first <c>open</c>, from
/// <see cref="BrowserExecutableDiscovery"/> and delegates every page action to that engine
/// (ADR 0055, amended 2026-11-22). The Chromium family (CDP) is preferred; Firefox uses
/// WebDriver BiDi. An explicit executable or <c>RU_BROWSER_EXE</c> pins the family. A run never
/// switches families after the first page, so the page contract and failure codes stay stable.
/// </summary>
public sealed class AutoBrowserEngine(string? preferredExecutable = null, bool persistentProfile = false) : IBrowserEngine
{
    private readonly object _gate = new();
    private IBrowserEngine? _inner;

    public bool IsAvailable => BrowserExecutableDiscovery.Resolve(preferredExecutable) is not null;

    public Task<BrowserPageHandle> OpenPageAsync(bool headless, CancellationToken cancellationToken) =>
        Inner().OpenPageAsync(headless, cancellationToken);

    public Task NavigateAsync(BrowserPageHandle page, string url, CancellationToken cancellationToken) =>
        Inner().NavigateAsync(page, url, cancellationToken);

    public Task<BrowserSnapshot> SnapshotAsync(
        BrowserPageHandle page,
        int maxElements,
        int maxTextChars,
        CancellationToken cancellationToken) =>
        Inner().SnapshotAsync(page, maxElements, maxTextChars, cancellationToken);

    public Task ClickAsync(BrowserPageHandle page, string elementRef, CancellationToken cancellationToken) =>
        Inner().ClickAsync(page, elementRef, cancellationToken);

    public Task TypeAsync(
        BrowserPageHandle page,
        string? elementRef,
        string text,
        bool replace,
        CancellationToken cancellationToken) =>
        Inner().TypeAsync(page, elementRef, text, replace, cancellationToken);

    public Task FillAsync(BrowserPageHandle page, string elementRef, string text, CancellationToken cancellationToken) =>
        Inner().FillAsync(page, elementRef, text, cancellationToken);

    public Task PressAsync(BrowserPageHandle page, string key, CancellationToken cancellationToken) =>
        Inner().PressAsync(page, key, cancellationToken);

    public Task<string> EvaluateAsync(BrowserPageHandle page, string expression, CancellationToken cancellationToken) =>
        Inner().EvaluateAsync(page, expression, cancellationToken);

    public Task<string> TitleAsync(BrowserPageHandle page, CancellationToken cancellationToken) =>
        Inner().TitleAsync(page, cancellationToken);

    public Task<IReadOnlyList<BrowserConsoleEntry>> ConsoleAsync(
        BrowserPageHandle page,
        int maxEntries,
        CancellationToken cancellationToken) =>
        Inner().ConsoleAsync(page, maxEntries, cancellationToken);

    public Task<IReadOnlyList<BrowserNetworkEntry>> NetworkAsync(
        BrowserPageHandle page,
        int maxEntries,
        CancellationToken cancellationToken) =>
        Inner().NetworkAsync(page, maxEntries, cancellationToken);

    public Task ClosePageAsync(BrowserPageHandle page, CancellationToken cancellationToken) =>
        Inner().ClosePageAsync(page, cancellationToken);

    public Task<byte[]> ScreenshotAsync(BrowserPageHandle page, string? elementRef, CancellationToken cancellationToken) =>
        Inner().ScreenshotAsync(page, elementRef, cancellationToken);

    public Task HoverAsync(BrowserPageHandle page, string elementRef, CancellationToken cancellationToken) =>
        Inner().HoverAsync(page, elementRef, cancellationToken);

    public void SetNextDialogAnswer(BrowserPageHandle page, bool accept, string? promptText) =>
        Inner().SetNextDialogAnswer(page, accept, promptText);

    public IReadOnlyList<BrowserDialog> TakeDialogs(BrowserPageHandle page) =>
        _inner is { } inner ? inner.TakeDialogs(page) : [];

    public IReadOnlyList<BrowserPageHandle> TakeOpenedPages(BrowserPageHandle page) =>
        _inner is { } inner ? inner.TakeOpenedPages(page) : [];

    public Task SetFilesAsync(BrowserPageHandle page, string elementRef, IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        Inner().SetFilesAsync(page, elementRef, paths, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        IBrowserEngine? inner;
        lock (_gate)
        {
            inner = _inner;
            _inner = null;
        }

        if (inner is not null) await inner.DisposeAsync().ConfigureAwait(false);
    }

    private IBrowserEngine Inner()
    {
        if (_inner is { } existing) return existing;
        lock (_gate)
        {
            if (_inner is { } again) return again;
            var target = BrowserExecutableDiscovery.Resolve(preferredExecutable)
                ?? throw new BrowserEngineException(
                    "browser_unavailable",
                    "No supported browser was found: install a Chromium browser (Edge, Chrome, Brave, Vivaldi, Opera, Chromium or Arc) or Firefox, or set RU_BROWSER_EXE.");
            _inner = target.Family == BrowserFamily.Firefox
                ? new FirefoxBrowserEngine(target.ExecutablePath, persistentProfile)
                : new ChromiumBrowserEngine(target.ExecutablePath, persistentProfile);
            return _inner;
        }
    }
}
