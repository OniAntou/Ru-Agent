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
}

/// <summary>A named browser failure the tool reports as a bounded code plus detail.</summary>
public sealed class BrowserEngineException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;

    public string Detail { get; } = detail;
}
