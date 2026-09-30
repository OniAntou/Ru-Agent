namespace RuAgent.SessionInterchange;

/// <summary>
/// The size bounds a session import lives under, shared by every surface that accepts one.
/// </summary>
/// <remarks>
/// The CLI and the GUI cap the file they are about to read; the host caps the body it is
/// willing to buffer and the content it is willing to parse. One set of constants keeps the
/// three surfaces from disagreeing about what "too large" means.
/// </remarks>
public static class SessionInterchangeLimits
{
    /// <summary>
    /// Largest session payload an import accepts, in UTF-8 bytes. Enforced on the file
    /// (CLI, GUI) and again on the decoded content (host), so a file that passes one
    /// surface's check cannot be refused as "too large" by another for a different reason.
    /// </summary>
    public const int MaxImportContentBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Transport bound for the import request body, sized for the worst-case JSON escaping
    /// of a full-cap content.
    /// </summary>
    /// <remarks>
    /// The client serializes with <c>System.Text.Json</c>'s default encoder, which writes
    /// HTML-sensitive, non-ASCII and most control characters as <c>\uXXXX</c> — six ASCII
    /// bytes per UTF-16 char. A char never carries fewer than one UTF-8 byte, so a content
    /// of N UTF-8 bytes can serialize to at most 6N plus the envelope. Any content the CLI
    /// and GUI accept (≤ <see cref="MaxImportContentBytes"/> UTF-8 bytes) therefore fits
    /// below this bound, and a body above it is refused before parsing rather than by a
    /// lower layer with a generic error.
    /// </remarks>
    public const int MaxImportBodyBytes = (MaxImportContentBytes * 6) + (1024 * 1024);

    /// <summary>
    /// Declared-body bound for the <c>POST /sessions/import</c> route, set one MiB above
    /// <see cref="MaxImportBodyBytes"/> so the request reaches the endpoint and the
    /// endpoint's own bounded reader answers with <c>import_too_large</c> rather than the
    /// generic transport rejection. Kestrel's own bound is layered above this one
    /// (<c>BackendLimits.KestrelMaxRequestBodyBytes</c>).
    /// </summary>
    public const int MaxImportRouteBodyBytes = MaxImportBodyBytes + (1024 * 1024);
}
