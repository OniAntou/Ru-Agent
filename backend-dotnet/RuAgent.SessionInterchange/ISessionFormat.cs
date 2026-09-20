namespace RuAgent.SessionInterchange;

/// <summary>
/// One agent's on-disk session format.
/// </summary>
/// <remarks>
/// Implementations are pure: they take text and give text back. They never touch the
/// filesystem, because where a file goes differs per partner (Claude Code keys a
/// directory by the project path, Codex keys one by date) and because a format that
/// decided its own destination could not be exercised by a test.
/// </remarks>
public interface ISessionFormat
{
    /// <summary>The id used on the command line and in an import request.</summary>
    string Id { get; }

    /// <summary>How the partner is named to a person.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether a session can be written in this format, so the partner can open it.
    /// </summary>
    /// <remarks>
    /// Reading is always possible: a file that exists can be parsed. This flag is about the
    /// other direction — handing a session *to* the partner — and a format can be readable
    /// without being writable when its canonical store is something we cannot produce.
    /// </remarks>
    bool CanWrite { get; }

    /// <summary>
    /// Why writing is refused, when it is. Empty for a format that can be written.
    /// </summary>
    /// <remarks>
    /// A refusal has to say which evidence is missing. "Unsupported" sends the reader
    /// looking for a flag that does not exist.
    /// </remarks>
    string WriteRefusal { get; }

    /// <summary>Reads one session file.</summary>
    /// <exception cref="InvalidDataException">The payload is not this format.</exception>
    SessionTranscript Read(string payload);

    /// <summary>
    /// Writes the files that make up a session in this format. The first file is the
    /// transcript; the rest are companions the partner needs to see it.
    /// </summary>
    IReadOnlyList<SessionOutputFile> Write(SessionTranscript transcript);
}

/// <summary>The session formats this build knows.</summary>
public static class SessionFormatCatalog
{
    /// <summary>Every known format, in the order they are offered to a person.</summary>
    public static IReadOnlyList<ISessionFormat> All { get; } =
    [
        new ClaudeCodeSessionFormat(),
        new CodexSessionFormat(),
        new AntigravitySessionFormat(),
        new OpenCodeSessionFormat(),
    ];

    /// <summary>Finds a format by id, or returns null.</summary>
    public static ISessionFormat? Find(string id) =>
        All.FirstOrDefault(format => string.Equals(format.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds a format by id, or throws with the ids that exist.
    /// </summary>
    /// <exception cref="ArgumentException">No format has that id.</exception>
    public static ISessionFormat Require(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A session format id is required.", nameof(id));
        }

        return Find(id) ?? throw new ArgumentException(
            $"Unknown session format '{id}'. Known formats: {string.Join(", ", All.Select(format => format.Id))}.",
            nameof(id));
    }
}
