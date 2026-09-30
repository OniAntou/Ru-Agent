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
    /// Transport bound for the import request body. JSON string escaping can double the
    /// content's size, so the body is allowed twice the content cap; the content cap is
    /// enforced again after parsing.
    /// </summary>
    public const int MaxImportBodyBytes = MaxImportContentBytes * 2 + (1024 * 1024);

    /// <summary>
    /// Declared-body bound for the <c>POST /sessions/import</c> route, set above
    /// <see cref="MaxImportBodyBytes"/> so the request reaches the endpoint and the
    /// endpoint's own bounded reader answers with <c>import_too_large</c> rather than the
    /// generic transport rejection.
    /// </summary>
    public const int MaxImportRouteBodyBytes = MaxImportBodyBytes + (1024 * 1024);
}
