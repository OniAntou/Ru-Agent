namespace RuAgent.SessionInterchange;

/// <summary>
/// Where one exported file goes, and whether it replaces or extends what is already there.
/// </summary>
public sealed record SessionFileTarget(string Path, bool Append);

/// <summary>
/// Where a partner keeps its session files.
/// </summary>
/// <remarks>
/// The adapters decide what a file contains; this decides where it goes, because the two
/// answers come from different places — the first is the partner's format, the second is
/// the partner's installation. Nothing here touches the filesystem: the caller passes the
/// home directory in, so the layout can be tested without a real profile.
///
/// A format with no known home throws rather than guessing one. Writing a session into the
/// wrong directory looks like success and is not.
/// </remarks>
public static class SessionFileLayout
{
    /// <summary>Claude Code's format id, as it appears on the command line.</summary>
    public const string ClaudeCodeId = "claude-code";

    /// <summary>Codex's format id, as it appears on the command line.</summary>
    public const string CodexId = "codex";

    /// <summary>Antigravity's format id, as it appears on the command line.</summary>
    public const string AntigravityId = "antigravity";

    /// <summary>opencode's format id, as it appears on the command line.</summary>
    public const string OpenCodeId = "opencode";

    /// <summary>
    /// Where each file of an export goes, in the order the format produced them.
    /// </summary>
    /// <param name="harness">The format id the export was asked for.</param>
    /// <param name="fileNames">The names the format produced, in its own order.</param>
    /// <param name="workingDirectory">The session's workspace, when one is known.</param>
    /// <param name="createdAt">When the session started; Codex files by date.</param>
    /// <param name="homeDirectory">The user profile to place the files under.</param>
    /// <exception cref="ArgumentException">The format has no known home.</exception>
    public static IReadOnlyList<SessionFileTarget> TargetsFor(
        string harness,
        IReadOnlyList<string> fileNames,
        string? workingDirectory,
        DateTimeOffset createdAt,
        string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harness);
        ArgumentNullException.ThrowIfNull(fileNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        if (fileNames.Count == 0)
        {
            throw new ArgumentException("An export with no files has nowhere to go.", nameof(fileNames));
        }

        return harness switch
        {
            ClaudeCodeId =>
            [
                new SessionFileTarget(
                    System.IO.Path.Combine(
                        homeDirectory,
                        ".claude",
                        "projects",
                        ProjectSlug(workingDirectory),
                        fileNames[0]),
                    Append: false),
            ],

            CodexId => CodexTargets(fileNames, createdAt, homeDirectory),

            _ => throw new ArgumentException(
                $"There is no known home for '{harness}' sessions, so nothing is written.",
                nameof(harness)),
        };
    }

    /// <summary>
    /// Which agent wrote a session file, judged from the name it was given.
    /// </summary>
    /// <remarks>
    /// Claude Code names a transcript after its session id and Codex names a rollout after
    /// the thread and the moment it started, so the name is usually enough. When it is not,
    /// this returns null rather than guessing a format that would then refuse the file —
    /// the caller can ask a person, who knows, unlike a heuristic.
    /// </remarks>
    public static string? HarnessFromFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var name = System.IO.Path.GetFileName(path);
        if (name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
        {
            return CodexId;
        }

        // Antigravity names its log the same thing in every conversation, which is thin
        // evidence but the only name it gives: the conversation id is the directory.
        if (string.Equals(name, "transcript.jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return AntigravityId;
        }

        return Guid.TryParse(System.IO.Path.GetFileNameWithoutExtension(name), out _) ? ClaudeCodeId : null;
    }

    /// <summary>
    /// The directory name Claude Code keys a project by: the working directory with the
    /// drive colon and every separator turned into a dash, so <c>D:\roulette-quiz</c>
    /// becomes <c>D--roulette-quiz</c>.
    /// </summary>
    public static string ProjectSlug(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return "unknown-project";
        }

        var trimmed = workingDirectory.TrimEnd('\\', '/');
        return string.Concat(trimmed.Select(character =>
            character is ':' or '\\' or '/' ? '-' : character));
    }

    private static IReadOnlyList<SessionFileTarget> CodexTargets(
        IReadOnlyList<string> fileNames,
        DateTimeOffset createdAt,
        string homeDirectory)
    {
        var stamp = createdAt == default ? DateTimeOffset.UtcNow : createdAt;
        var day = stamp
            .ToUniversalTime()
            .ToString("yyyy/MM/dd")
            .Replace('/', System.IO.Path.DirectorySeparatorChar);

        var targets = new List<SessionFileTarget>(2)
        {
            new(
                System.IO.Path.Combine(homeDirectory, ".codex", "sessions", day, fileNames[0]),
                Append: false),
        };

        if (fileNames.Count > 1)
        {
            // The picker index is append-only: it holds every thread, so replacing it
            // would erase the sessions already listed in it.
            targets.Add(new SessionFileTarget(
                System.IO.Path.Combine(homeDirectory, ".codex", fileNames[1]),
                Append: true));
        }

        return targets;
    }
}
