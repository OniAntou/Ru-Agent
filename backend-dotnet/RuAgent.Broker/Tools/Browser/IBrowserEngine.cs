namespace RuAgent.Broker.Tools.Browser;

/// <summary>One CDP page target attached with a flattened session id.</summary>
public sealed record BrowserPageHandle(string TargetId, string SessionId);

/// <summary>One interactive element in a page snapshot; <see cref="ElementRef"/> is what click/type take.</summary>
public sealed record BrowserSnapshotElement(
    string ElementRef,
    string Role,
    string Name,
    string Value,
    bool Checked,
    bool Disabled);

public sealed record BrowserSnapshot(
    string Url,
    string Title,
    IReadOnlyList<BrowserSnapshotElement> Elements,
    string Text,
    bool TextTruncated);

public sealed record BrowserConsoleEntry(string Level, string Text);

public sealed record BrowserNetworkEntry(string Method, string Url, int? Status, string ResourceType);

/// <summary>A JavaScript dialog the engine answered: its kind, bounded message and the answer it gave.</summary>
public sealed record BrowserDialog(string Kind, string Message, bool Accepted);

/// <summary>
/// The browser seam: one isolated page driven over a direct browser protocol (ADR 0055): the
/// Chrome DevTools Protocol for the Chromium family (Edge, Chrome, Brave, Vivaldi, Opera,
/// Chromium, Arc) or WebDriver BiDi for Firefox. The service owns session grants and bounds;
/// the engine owns the browser process and pages.
/// </summary>
public interface IBrowserEngine : IAsyncDisposable
{
    /// <summary>True when an executable is configured or an installed Edge/Chrome was found.</summary>
    bool IsAvailable { get; }

    Task<BrowserPageHandle> OpenPageAsync(bool headless, CancellationToken cancellationToken);

    Task NavigateAsync(BrowserPageHandle page, string url, CancellationToken cancellationToken);

    Task<BrowserSnapshot> SnapshotAsync(
        BrowserPageHandle page,
        int maxElements,
        int maxTextChars,
        CancellationToken cancellationToken);

    Task ClickAsync(BrowserPageHandle page, string elementRef, CancellationToken cancellationToken);

    /// <summary>Focuses <paramref name="elementRef"/> when present and inserts text; replace selects first.</summary>
    Task TypeAsync(BrowserPageHandle page, string? elementRef, string text, bool replace, CancellationToken cancellationToken);

    Task FillAsync(BrowserPageHandle page, string elementRef, string text, CancellationToken cancellationToken);

    Task PressAsync(BrowserPageHandle page, string key, CancellationToken cancellationToken);

    Task<string> EvaluateAsync(BrowserPageHandle page, string expression, CancellationToken cancellationToken);

    Task<string> TitleAsync(BrowserPageHandle page, CancellationToken cancellationToken);

    Task<IReadOnlyList<BrowserConsoleEntry>> ConsoleAsync(
        BrowserPageHandle page,
        int maxEntries,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BrowserNetworkEntry>> NetworkAsync(
        BrowserPageHandle page,
        int maxEntries,
        CancellationToken cancellationToken);

    Task ClosePageAsync(BrowserPageHandle page, CancellationToken cancellationToken);

    /// <summary>A PNG of the viewport, or of one snapshot ref scrolled into view.</summary>
    Task<byte[]> ScreenshotAsync(BrowserPageHandle page, string? elementRef, CancellationToken cancellationToken);

    /// <summary>Moves the trusted pointer over a snapshot ref so hover menus and tooltips open.</summary>
    Task HoverAsync(BrowserPageHandle page, string elementRef, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the answer for the page's next JavaScript dialog only. Without one, alert and
    /// beforeunload are accepted and confirm and prompt are dismissed, so a dialog never blocks.
    /// </summary>
    void SetNextDialogAnswer(BrowserPageHandle page, bool accept, string? promptText);

    /// <summary>Dialogs answered on the page since the last call, oldest first.</summary>
    IReadOnlyList<BrowserDialog> TakeDialogs(BrowserPageHandle page);

    /// <summary>Pages the page opened itself (window.open, target=_blank) since the last call.</summary>
    IReadOnlyList<BrowserPageHandle> TakeOpenedPages(BrowserPageHandle page);

    /// <summary>Sets the files of a snapshot ref that is an &lt;input type=file&gt;; paths are already authorized.</summary>
    Task SetFilesAsync(BrowserPageHandle page, string elementRef, IReadOnlyList<string> paths, CancellationToken cancellationToken);
}

/// <summary>A named browser failure the tool reports as a bounded code plus detail.</summary>
public sealed class BrowserEngineException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;

    public string Detail { get; } = detail;
}
